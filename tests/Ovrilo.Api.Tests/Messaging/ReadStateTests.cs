using System.Net;
using System.Net.Http.Json;
using System.Threading.Channels;

using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

using Ovrilo.Api.Tests.Communities;
using Ovrilo.Api.Tests.Identity;
using Ovrilo.Modules.Messaging.Features;

namespace Ovrilo.Api.Tests.Messaging;

[Collection(SharedApiFixture.Name)]
public sealed class ReadStateTests(OvriloApiFactory factory)
{
    [Fact]
    public async Task UnreadCountsOtherPeoplesMessagesUntilMarkedRead()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        using var member = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();
        await member.AcceptAsync((await owner.CreateInvitationAsync(community.Id)).Code);
        var general = await owner.CreateChannelAsync(community.Id, "general", "text");
        var quiet = await owner.CreateChannelAsync(community.Id, "quiet", "text");

        var first = await SendAsync(owner, general.Id, "one");
        await SendAsync(owner, general.Id, "two");
        var mine = await SendAsync(member, general.Id, "mine");

        // Writing advanced the member's own position: only what came before is unread.
        var unread = await UnreadAsync(member, community.Id);
        Assert.Equal([general.Id, quiet.Id], unread.Select(u => u.ChannelId));
        Assert.Equal(new ChannelUnreadResponse(general.Id, mine.Id, mine.Id, 0), unread[0]);
        Assert.Equal(new ChannelUnreadResponse(quiet.Id, null, null, 0), unread[1]);

        var later = await SendAsync(owner, general.Id, "three");
        await SendAsync(owner, general.Id, "four");
        Assert.Equal(2, (await UnreadAsync(member, community.Id))[0].UnreadCount);
        // The owner's own newer messages cover the member's reply.
        Assert.Equal(0, (await UnreadAsync(owner, community.Id))[0].UnreadCount);

        Assert.Equal(later.Id, (await MarkReadAsync(member, general.Id, later.Id)).LastReadMessageId);
        Assert.Equal(1, (await UnreadAsync(member, community.Id))[0].UnreadCount);

        // An older position from another device never moves it back.
        Assert.Equal(later.Id, (await MarkReadAsync(member, general.Id, first.Id)).LastReadMessageId);
    }

    [Fact]
    public async Task MarkersMustBelongToTheChannelAndOutsidersSeeNothing()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        using var stranger = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();
        var general = await owner.CreateChannelAsync(community.Id, "general", "text");
        var other = await owner.CreateChannelAsync(community.Id, "other", "text");
        var elsewhere = await SendAsync(owner, other.Id, "elsewhere");

        var wrongChannel = await owner.Client.PutAsJsonAsync(new Uri($"/channels/{general.Id}/read-state", UriKind.Relative), new MarkReadRequest(elsewhere.Id));
        Assert.Equal(HttpStatusCode.BadRequest, wrongChannel.StatusCode);

        var strangerMark = await stranger.Client.PutAsJsonAsync(new Uri($"/channels/{other.Id}/read-state", UriKind.Relative), new MarkReadRequest(elsewhere.Id));
        Assert.Equal(HttpStatusCode.NotFound, strangerMark.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/communities/{community.Id}/unread")).StatusCode);
    }

    [Fact]
    public async Task OtherDevicesOfTheReaderAreTold()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        using var member = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();
        await member.AcceptAsync((await owner.CreateInvitationAsync(community.Id)).Code);
        var general = await owner.CreateChannelAsync(community.Id, "general", "text");
        var message = await SendAsync(owner, general.Id, "read me");

        await using var otherDevice = await ConnectAsync(member);
        await using var someoneElse = await ConnectAsync(owner);
        var events = Channel.CreateUnbounded<ReadStateResponse>();
        var leaked = Channel.CreateUnbounded<ReadStateResponse>();
        otherDevice.On<ReadStateResponse>(ReadStateEvents.ReadStateUpdated, e => events.Writer.TryWrite(e));
        someoneElse.On<ReadStateResponse>(ReadStateEvents.ReadStateUpdated, e => leaked.Writer.TryWrite(e));

        await MarkReadAsync(member, general.Id, message.Id);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Equal(new ReadStateResponse(general.Id, message.Id), await events.Reader.ReadAsync(timeout.Token));
        await Task.Delay(300);
        Assert.False(leaked.Reader.TryRead(out _));
    }

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

    private static async Task<MessageResponse> SendAsync(TestUser user, Guid channelId, string content)
    {
        var response = await user.PostAsync($"/channels/{channelId}/messages", new SendMessageRequest(Guid.NewGuid(), content, null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MessageResponse>(TestJson.Options))!;
    }

    private static async Task<List<ChannelUnreadResponse>> UnreadAsync(TestUser user, Guid communityId)
    {
        var response = await user.GetAsync($"/communities/{communityId}/unread");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<ChannelUnreadResponse>>(TestJson.Options))!;
    }

    private static async Task<ReadStateResponse> MarkReadAsync(TestUser user, Guid channelId, long messageId)
    {
        var response = await user.Client.PutAsJsonAsync(new Uri($"/channels/{channelId}/read-state", UriKind.Relative), new MarkReadRequest(messageId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ReadStateResponse>(TestJson.Options))!;
    }
}
