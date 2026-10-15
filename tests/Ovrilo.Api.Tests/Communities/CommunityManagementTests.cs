using System.Net;
using System.Net.Http.Json;
using System.Threading.Channels;

using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

using Ovrilo.Api.Tests.Identity;
using Ovrilo.Modules.Communities.Features;

namespace Ovrilo.Api.Tests.Communities;

/// <summary>Renaming and deleting communities (owner tools).</summary>
[Collection(SharedApiFixture.Name)]
public sealed class CommunityManagementTests(OvriloApiFactory factory)
{
    [Fact]
    public async Task OwnerRenamesTheCommunityAndMembersAreTold()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        using var member = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync("Old name");
        await member.AcceptAsync((await owner.CreateInvitationAsync(community.Id)).Code);
        await using var connection = await ConnectAsync(member);
        var updates = Channel.CreateUnbounded<CommunityUpdatedEvent>();
        connection.On<CommunityUpdatedEvent>(CommunityEvents.CommunityUpdated, e => updates.Writer.TryWrite(e));
        await connection.InvokeAsync<object>("SubscribeCommunity", community.Id);

        var response = await PatchAsync(owner, community.Id, new UpdateCommunityRequest("  New name  ", null));
        var renamed = (await response.Content.ReadFromJsonAsync<CommunityResponse>(TestJson.Options))!;

        Assert.Equal(("New name", "Test community"), (renamed.Name, renamed.Description));
        Assert.Equal(new CommunityUpdatedEvent(community.Id, "New name", "Test community", null), await NextAsync(updates));

        // An empty description clears it; omitted fields stay.
        var cleared = await (await PatchAsync(owner, community.Id, new UpdateCommunityRequest(null, ""))).Content.ReadFromJsonAsync<CommunityResponse>(TestJson.Options);
        Assert.Equal(("New name", (string?)null), (cleared!.Name, cleared.Description));
    }

    [Fact]
    public async Task OnlyManagersRenameAndNamesAreValidated()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        using var member = await TestUser.RegisterAsync(factory);
        using var stranger = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();
        await member.AcceptAsync((await owner.CreateInvitationAsync(community.Id)).Code);

        Assert.Equal(HttpStatusCode.Forbidden, (await PatchAsync(member, community.Id, new UpdateCommunityRequest("Mine", null))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PatchAsync(stranger, community.Id, new UpdateCommunityRequest("Mine", null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PatchAsync(owner, community.Id, new UpdateCommunityRequest("   ", null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PatchAsync(owner, community.Id, new UpdateCommunityRequest(null, new string('x', 501)))).StatusCode);
    }

    [Fact]
    public async Task OwnerDeletesTheCommunityForEveryone()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        using var member = await TestUser.RegisterAsync(factory);
        using var latecomer = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();
        await member.AcceptAsync((await owner.CreateInvitationAsync(community.Id)).Code);
        var spareInvitation = await owner.CreateInvitationAsync(community.Id);
        var general = await owner.DefaultChannelAsync(community.Id, "text");
        await using var connection = await ConnectAsync(member);
        var deletions = Channel.CreateUnbounded<CommunityDeletedEvent>();
        connection.On<CommunityDeletedEvent>(CommunityEvents.CommunityDeleted, e => deletions.Writer.TryWrite(e));
        await connection.InvokeAsync<object>("SubscribeCommunity", community.Id);

        Assert.Equal(HttpStatusCode.Forbidden, (await member.Client.DeleteAsync(Uri($"/communities/{community.Id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.Client.DeleteAsync(Uri($"/communities/{community.Id}"))).StatusCode);

        Assert.Equal(new CommunityDeletedEvent(community.Id), await NextAsync(deletions));
        foreach (var user in new[] { owner, member })
        {
            var mine = await user.Client.GetFromJsonAsync<List<CommunityResponse>>(Uri("/communities"), TestJson.Options);
            Assert.DoesNotContain(mine!, c => c.Id == community.Id);
            Assert.Equal(HttpStatusCode.NotFound, (await user.GetAsync($"/communities/{community.Id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await user.GetAsync($"/communities/{community.Id}/channels")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await user.GetAsync($"/channels/{general.Id}/messages")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await latecomer.AcceptAsync(spareInvitation.Code)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Client.DeleteAsync(Uri($"/communities/{community.Id}"))).StatusCode);
    }

    private static Uri Uri(string path) => new(path, UriKind.Relative);

    private static Task<HttpResponseMessage> PatchAsync(TestUser user, Guid communityId, UpdateCommunityRequest body) =>
        user.Client.PatchAsJsonAsync(Uri($"/communities/{communityId}"), body);

    private async Task<HubConnection> ConnectAsync(TestUser user)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "hubs/realtime"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(user.Auth.AccessToken);
            })
            .Build();
        await connection.StartAsync();
        return connection;
    }

    private static async Task<T> NextAsync<T>(Channel<T> events)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await events.Reader.ReadAsync(timeout.Token);
    }
}
