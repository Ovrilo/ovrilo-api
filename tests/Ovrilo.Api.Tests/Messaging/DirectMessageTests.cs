using System.Net;
using System.Net.Http.Json;
using System.Threading.Channels;

using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

using Ovrilo.Api.Tests.Communities;
using Ovrilo.Api.Tests.Identity;
using Ovrilo.Modules.Messaging.Features;
using Ovrilo.Modules.Social.Features;

namespace Ovrilo.Api.Tests.Messaging;

[Collection(SharedApiFixture.Name)]
public sealed class DirectMessageTests(OvriloApiFactory factory)
{
    private static async Task MakeFriendsAsync(TestUser a, TestUser b)
    {
        await a.PostAsync("/friends/requests", new SendFriendRequestRequest(b.Auth.User.Username));
        var reply = await b.PostAsync("/friends/requests", new SendFriendRequestRequest(a.Auth.User.Username));
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
    }

    private static async Task<(HttpStatusCode Status, DirectConversationResponse? Body)> OpenAsync(TestUser user, Guid otherId)
    {
        var response = await user.PostAsync("/dms", new OpenDirectMessageRequest(otherId));
        return (response.StatusCode, response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<DirectConversationResponse>(TestJson.Options) : null);
    }

    private static Task<HttpResponseMessage> SendAsync(TestUser user, Guid conversationId, string text) =>
        user.PostAsync($"/channels/{conversationId}/messages", new SendMessageRequest(Guid.NewGuid(), text, null));

    private static async Task<List<DirectConversationResponse>> ListAsync(TestUser user) =>
        (await (await user.GetAsync("/dms")).Content.ReadFromJsonAsync<List<DirectConversationResponse>>(TestJson.Options))!;

    [Fact]
    public async Task FriendsTalkPrivatelyWithUnreadCounts()
    {
        using var ada = await TestUser.RegisterAsync(factory);
        using var bob = await TestUser.RegisterAsync(factory);
        using var eve = await TestUser.RegisterAsync(factory);
        await MakeFriendsAsync(ada, bob);

        var opened = await OpenAsync(ada, bob.Id);
        Assert.Equal(HttpStatusCode.Created, opened.Status);
        var dm = opened.Body!;
        Assert.Equal(bob.Id, dm.User.Id);
        // The same conversation from either side.
        Assert.Equal((HttpStatusCode.OK, dm.Id), ((await OpenAsync(bob, ada.Id)).Status, (await OpenAsync(bob, ada.Id)).Body!.Id));

        Assert.Equal(HttpStatusCode.Created, (await SendAsync(ada, dm.Id, "hi bob")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(ada, dm.Id, "are you there?")).StatusCode);

        var bobs = Assert.Single(await ListAsync(bob));
        Assert.Equal((dm.Id, ada.Id, 2), (bobs.Id, bobs.User.Id, bobs.UnreadCount));
        Assert.Equal(0, Assert.Single(await ListAsync(ada)).UnreadCount);

        var history = await (await bob.GetAsync($"/channels/{dm.Id}/messages")).Content.ReadFromJsonAsync<MessagePageResponse>(TestJson.Options);
        Assert.Equal(["hi bob", "are you there?"], history!.Messages.Select(m => m.Content));
        await bob.Client.PutAsJsonAsync(new Uri($"/channels/{dm.Id}/read-state", UriKind.Relative), new MarkReadRequest(history.Messages[^1].Id));
        Assert.Equal(0, Assert.Single(await ListAsync(bob)).UnreadCount);

        // Outsiders see nothing.
        Assert.Equal(HttpStatusCode.NotFound, (await eve.GetAsync($"/channels/{dm.Id}/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(eve, dm.Id, "let me in")).StatusCode);
        Assert.Empty(await ListAsync(eve));

        // Nobody deletes the other person's messages.
        var adasMessage = history.Messages[0].Id;
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.Client.DeleteAsync(new Uri($"/messages/{adasMessage}", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task OnlyFriendsCanStartOrKeepWriting()
    {
        using var ada = await TestUser.RegisterAsync(factory);
        using var bob = await TestUser.RegisterAsync(factory);

        Assert.Equal(HttpStatusCode.Forbidden, (await OpenAsync(ada, bob.Id)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await OpenAsync(ada, ada.Id)).Status);

        await MakeFriendsAsync(ada, bob);
        var dm = (await OpenAsync(ada, bob.Id)).Body!;
        await SendAsync(bob, dm.Id, "before");

        await ada.Client.DeleteAsync(new Uri($"/friends/{bob.Id}", UriKind.Relative));
        // The history stays readable, but nobody can write.
        Assert.Equal(HttpStatusCode.OK, (await ada.GetAsync($"/channels/{dm.Id}/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(bob, dm.Id, "after")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await OpenAsync(ada, bob.Id)).Status);

        await MakeFriendsAsync(ada, bob);
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(bob, dm.Id, "friends again")).StatusCode);
        await bob.Client.PutAsync(new Uri($"/blocks/{ada.Id}", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(ada, dm.Id, "blocked")).StatusCode);
    }

    [Fact]
    public async Task NewMessagesReachTheRecipientWithoutOpeningTheConversation()
    {
        using var ada = await TestUser.RegisterAsync(factory);
        using var bob = await TestUser.RegisterAsync(factory);
        await MakeFriendsAsync(ada, bob);
        var dm = (await OpenAsync(ada, bob.Id)).Body!;

        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "hubs/realtime"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(bob.Auth.AccessToken);
            })
            .Build();
        var created = Channel.CreateUnbounded<MessageResponse>();
        connection.On<MessageResponse>(MessagingEvents.MessageCreated, m => created.Writer.TryWrite(m));
        await connection.StartAsync();

        await SendAsync(ada, dm.Id, "ping");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var message = await created.Reader.ReadAsync(timeout.Token);
        Assert.Equal((dm.Id, "ping"), (message.ChannelId, message.Content));
    }
}
