using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

using Ovrilo.Api.Tests.Identity;
using Ovrilo.Modules.Channels.Contracts;
using Ovrilo.Modules.Channels.Features;

namespace Ovrilo.Api.Tests.Communities;

[Collection(SharedApiFixture.Name)]
public sealed class ChannelTests(OvriloApiFactory factory)
{
    [Fact]
    public async Task NewCommunitiesStartWithGeneralTextAndVoiceChannels()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        using var friend = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();
        await friend.AcceptAsync((await owner.CreateInvitationAsync(community.Id)).Code);

        var channels = await friend.ListChannelsAsync(community.Id);

        Assert.Equal([("general", ChannelType.Text, 0), ("General", ChannelType.Voice, 0)], channels.Select(c => (c.Name, c.Type, c.Position)));
        Assert.All(channels, c => Assert.Null(c.Topic));

        // Only a community without channels gets them.
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IChannelsModule>().CreateDefaultChannelsAsync(community.Id, owner.Id);
        Assert.Equal(2, (await friend.ListChannelsAsync(community.Id)).Count);
    }

    [Fact]
    public async Task OwnerCreatesChannelsAndMembersSeeThem()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        using var friend = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();
        await friend.AcceptAsync((await owner.CreateInvitationAsync(community.Id)).Code);
        var general = await owner.DefaultChannelAsync(community.Id, "text");
        var generalVoice = await owner.DefaultChannelAsync(community.Id, "voice");

        var clips = await owner.CreateChannelAsync(community.Id, "clips", "text");
        var lobby = await owner.CreateChannelAsync(community.Id, "Lobby", "voice");

        var channels = await friend.ListChannelsAsync(community.Id);
        Assert.Equal([general.Id, clips.Id, generalVoice.Id, lobby.Id], channels.Select(c => c.Id));
        Assert.Equal([0, 1, 0, 1], channels.Select(c => c.Position));
        Assert.Equal(ChannelType.Voice, lobby.Type);

        var single = await friend.GetAsync($"/channels/{general.Id}");
        Assert.Equal(HttpStatusCode.OK, single.StatusCode);
    }

    [Fact]
    public async Task NonMembersCannotListOrReadChannels()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        using var stranger = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();
        var channel = await owner.DefaultChannelAsync(community.Id, "text");

        var list = await stranger.GetAsync($"/communities/{community.Id}/channels");
        var get = await stranger.GetAsync($"/channels/{channel.Id}");
        var create = await stranger.PostAsync($"/communities/{community.Id}/channels", new { name = "x", type = "text" });

        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal("channels.not_found", await get.ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.NotFound, create.StatusCode);
    }

    [Fact]
    public async Task MembersCannotManageChannels()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        using var friend = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();
        await friend.AcceptAsync((await owner.CreateInvitationAsync(community.Id)).Code);
        var channel = await owner.DefaultChannelAsync(community.Id, "text");

        var create = await friend.PostAsync($"/communities/{community.Id}/channels", new { name = "mine", type = "text" });
        var update = await friend.Client.PatchAsJsonAsync(new Uri($"/channels/{channel.Id}", UriKind.Relative), new UpdateChannelRequest("renamed", null, null));
        var delete = await friend.Client.DeleteAsync(new Uri($"/channels/{channel.Id}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal("channels.permission_denied", await create.ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }

    [Fact]
    public async Task ChannelNamesAreUniquePerTypeIgnoringCase()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();
        await owner.CreateChannelAsync(community.Id, "Clips", "text");

        var duplicate = await owner.PostAsync($"/communities/{community.Id}/channels", new { name = "clips", type = "text" });
        var otherType = await owner.PostAsync($"/communities/{community.Id}/channels", new { name = "clips", type = "voice" });
        var defaultName = await owner.PostAsync($"/communities/{community.Id}/channels", new { name = "GENERAL", type = "voice" });

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("channels.name_taken", await duplicate.ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.Created, otherType.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, defaultName.StatusCode);
    }

    [Fact]
    public async Task OwnerUpdatesAndDeletesChannel()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();
        var channel = await owner.DefaultChannelAsync(community.Id, "text");

        // A position past the end means last; it is the only text channel.
        var update = await owner.Client.PatchAsJsonAsync(new Uri($"/channels/{channel.Id}", UriKind.Relative), new UpdateChannelRequest("chat", "Daily chatter", 3));
        var updated = (await update.Content.ReadFromJsonAsync<ChannelResponse>(TestJson.Options))!;
        var delete = await owner.Client.DeleteAsync(new Uri($"/channels/{channel.Id}", UriKind.Relative));
        var afterDelete = await owner.GetAsync($"/channels/{channel.Id}");
        var recreate = await owner.PostAsync($"/communities/{community.Id}/channels", new { name = "chat", type = "text" });

        Assert.Equal(("chat", "Daily chatter", 0), (updated.Name, updated.Topic, updated.Position));
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, afterDelete.StatusCode);
        Assert.Equal(HttpStatusCode.Created, recreate.StatusCode);
    }

    [Theory]
    [InlineData("", "text")]
    [InlineData("valid", "video")]
    public async Task InvalidChannelInputIsRejected(string name, string type)
    {
        using var owner = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();

        var response = await owner.PostAsync($"/communities/{community.Id}/channels", new { name, type });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ChannelsContractReflectsMembershipAndChannelType()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        using var stranger = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();
        var text = await owner.DefaultChannelAsync(community.Id, "text");
        var voice = await owner.DefaultChannelAsync(community.Id, "voice");

        await using var scope = factory.Services.CreateAsyncScope();
        var channels = scope.ServiceProvider.GetRequiredService<IChannelsModule>();

        Assert.True(await channels.CanReadChannelAsync(text.Id, owner.Id));
        Assert.True(await channels.CanWriteMessagesAsync(text.Id, owner.Id));
        Assert.False(await channels.CanWriteMessagesAsync(voice.Id, owner.Id));
        Assert.True(await channels.CanJoinVoiceAsync(voice.Id, owner.Id));
        Assert.False(await channels.CanJoinVoiceAsync(text.Id, owner.Id));
        Assert.False(await channels.CanReadChannelAsync(text.Id, stranger.Id));
        Assert.False(await channels.CanJoinVoiceAsync(voice.Id, stranger.Id));
    }

    [Fact]
    public async Task MovingAChannelRenumbersOnlyItsType()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();
        var general = await owner.DefaultChannelAsync(community.Id, "text");
        var generalVoice = await owner.DefaultChannelAsync(community.Id, "voice");
        var clips = await owner.CreateChannelAsync(community.Id, "clips", "text");
        var memes = await owner.CreateChannelAsync(community.Id, "memes", "text");
        var lobby = await owner.CreateChannelAsync(community.Id, "Lobby", "voice");

        var toTop = await MoveAsync(owner, memes.Id, 0);
        Assert.Equal(0, toTop.Position);
        Assert.Equal(
            [(memes.Id, 0), (general.Id, 1), (clips.Id, 2), (generalVoice.Id, 0), (lobby.Id, 1)],
            (await owner.ListChannelsAsync(community.Id)).Select(c => (c.Id, c.Position)));

        // Past the end means last.
        Assert.Equal(2, (await MoveAsync(owner, memes.Id, 99)).Position);
        Assert.Equal([general.Id, clips.Id, memes.Id, generalVoice.Id, lobby.Id], (await owner.ListChannelsAsync(community.Id)).Select(c => c.Id));

        var negative = await owner.Client.PatchAsJsonAsync(new Uri($"/channels/{memes.Id}", UriKind.Relative), new UpdateChannelRequest(null, null, -1));
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
    }

    [Fact]
    public async Task DeletedChannelsLeaveNoGapForLaterMoves()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();
        var general = await owner.DefaultChannelAsync(community.Id, "text");
        var clips = await owner.CreateChannelAsync(community.Id, "clips", "text");
        var memes = await owner.CreateChannelAsync(community.Id, "memes", "text");

        await owner.DeleteChannelAsync(clips.Id);
        Assert.Equal(1, (await MoveAsync(owner, general.Id, 1)).Position);

        var text = (await owner.ListChannelsAsync(community.Id)).Where(c => c.Type == ChannelType.Text);
        Assert.Equal([(memes.Id, 0), (general.Id, 1)], text.Select(c => (c.Id, c.Position)));
    }

    [Fact]
    public async Task MembersAreToldWhenChannelsChange()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        using var member = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();
        await member.AcceptAsync((await owner.CreateInvitationAsync(community.Id)).Code);

        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "hubs/realtime"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(member.Auth.AccessToken);
            })
            // Strict like the server: the change must arrive as a camelCase string.
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false)))
            .Build();
        var events = Channel.CreateUnbounded<ChannelsChangedEvent>();
        connection.On<ChannelsChangedEvent>(ChannelEvents.ChannelsChanged, e => events.Writer.TryWrite(e));
        await connection.StartAsync();
        await connection.InvokeAsync<object>("SubscribeCommunity", community.Id);

        var clips = await owner.CreateChannelAsync(community.Id, "clips", "text");
        Assert.Equal(new ChannelsChangedEvent(community.Id, clips.Id, ChannelChange.Created), await NextAsync(events));

        await MoveAsync(owner, clips.Id, 0);
        Assert.Equal(new ChannelsChangedEvent(community.Id, clips.Id, ChannelChange.Updated), await NextAsync(events));

        await owner.DeleteChannelAsync(clips.Id);
        Assert.Equal(new ChannelsChangedEvent(community.Id, clips.Id, ChannelChange.Deleted), await NextAsync(events));
    }

    private static async Task<ChannelResponse> MoveAsync(TestUser user, Guid channelId, int position)
    {
        var response = await user.Client.PatchAsJsonAsync(new Uri($"/channels/{channelId}", UriKind.Relative), new UpdateChannelRequest(null, null, position));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ChannelResponse>(TestJson.Options))!;
    }

    private static async Task<ChannelsChangedEvent> NextAsync(Channel<ChannelsChangedEvent> events)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await events.Reader.ReadAsync(timeout.Token);
    }
}
