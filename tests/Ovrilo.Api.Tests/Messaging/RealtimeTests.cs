using System.Net;
using System.Net.Http.Json;
using System.Threading.Channels;

using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

using Ovrilo.Api.Tests.Communities;
using Ovrilo.Api.Tests.Identity;
using Ovrilo.Modules.Messaging.Features;

namespace Ovrilo.Api.Tests.Messaging;

/// <summary>The realtime hub over the in-memory test server (long polling; same hub code as WebSockets).</summary>
[Collection(SharedApiFixture.Name)]
public sealed class RealtimeTests(OvriloApiFactory factory)
{
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

    [Fact]
    public async Task SubscribersReceiveCreatedEditedAndDeletedMessages()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        using var member = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();
        await member.AcceptAsync((await owner.CreateInvitationAsync(community.Id)).Code);
        var general = await owner.CreateChannelAsync(community.Id, "general", "text");

        await using var connection = await ConnectAsync(member);
        var events = Channel.CreateUnbounded<(string Name, MessageResponse Message)>();
        foreach (var name in new[] { MessagingEvents.MessageCreated, MessagingEvents.MessageEdited, MessagingEvents.MessageDeleted })
        {
            connection.On<MessageResponse>(name, message => events.Writer.TryWrite((name, message)));
        }

        Assert.True(await connection.InvokeAsync<bool>("SubscribeChannel", general.Id));

        await owner.PostAsync($"/channels/{general.Id}/messages", new SendMessageRequest(Guid.NewGuid(), "live!", null));
        var created = await NextAsync(events);
        Assert.Equal(MessagingEvents.MessageCreated, created.Name);
        Assert.Equal("live!", created.Message.Content);
        Assert.Equal(owner.Auth.User.DisplayName, created.Message.Author.DisplayName);

        var id = created.Message.Id;
        await owner.Client.PatchAsJsonAsync(new Uri($"/messages/{id}", UriKind.Relative), new EditMessageRequest("edited"));
        var edited = await NextAsync(events);
        Assert.Equal((MessagingEvents.MessageEdited, "edited"), (edited.Name, edited.Message.Content));

        await owner.Client.DeleteAsync(new Uri($"/messages/{id}", UriKind.Relative));
        var deleted = await NextAsync(events);
        Assert.Equal(MessagingEvents.MessageDeleted, deleted.Name);
        Assert.Null(deleted.Message.Content);

        // A retried send is not a new message: no second event.
        var clientId = Guid.NewGuid();
        await owner.PostAsync($"/channels/{general.Id}/messages", new SendMessageRequest(clientId, "once", null));
        await NextAsync(events);
        await owner.PostAsync($"/channels/{general.Id}/messages", new SendMessageRequest(clientId, "once", null));
        Assert.False(await HasEventWithinAsync(events, TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task OutsidersCannotSubscribeAndReceiveNothing()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        using var stranger = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();
        var general = await owner.CreateChannelAsync(community.Id, "general", "text");

        await using var connection = await ConnectAsync(stranger);
        var events = Channel.CreateUnbounded<(string Name, MessageResponse Message)>();
        connection.On<MessageResponse>(MessagingEvents.MessageCreated, m => events.Writer.TryWrite((MessagingEvents.MessageCreated, m)));

        Assert.False(await connection.InvokeAsync<bool>("SubscribeChannel", general.Id));
        await owner.PostAsync($"/channels/{general.Id}/messages", new SendMessageRequest(Guid.NewGuid(), "secret", null));

        Assert.False(await HasEventWithinAsync(events, TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task ConnectionsRequireAnAccessToken()
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "hubs/realtime"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            })
            .Build();

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => connection.StartAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
    }

    private static async Task<(string Name, MessageResponse Message)> NextAsync(Channel<(string Name, MessageResponse Message)> events)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await events.Reader.ReadAsync(timeout.Token);
    }

    private static async Task<bool> HasEventWithinAsync(Channel<(string Name, MessageResponse Message)> events, TimeSpan wait)
    {
        using var timeout = new CancellationTokenSource(wait);
        try
        {
            await events.Reader.ReadAsync(timeout.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
