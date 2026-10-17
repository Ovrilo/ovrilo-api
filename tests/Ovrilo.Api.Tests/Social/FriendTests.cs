using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

using Ovrilo.Api.Realtime;
using Ovrilo.Api.Tests.Communities;
using Ovrilo.Api.Tests.Identity;
using Ovrilo.Modules.Social.Features;

namespace Ovrilo.Api.Tests.Social;

[Collection(SharedApiFixture.Name)]
public sealed class FriendTests(OvriloApiFactory factory)
{
    [Fact]
    public async Task RequestAcceptAndRemove()
    {
        using var ada = await TestUser.RegisterAsync(factory);
        using var bob = await TestUser.RegisterAsync(factory);

        var sent = await SendAsync(ada, bob.Auth.User.Username.ToUpperInvariant());
        Assert.Equal(HttpStatusCode.Created, sent.Status);
        Assert.Equal(FriendRequestOutcome.Requested, sent.Body!.Outcome);
        Assert.Equal(bob.Id, sent.Body.Request!.User.Id);

        // Sending again returns the pending request.
        var again = await SendAsync(ada, bob.Auth.User.Username);
        Assert.Equal((HttpStatusCode.OK, sent.Body.Request.Id), (again.Status, again.Body!.Request!.Id));

        var bobs = await RequestsAsync(bob);
        Assert.Equal([ada.Id], bobs.Incoming.Select(r => r.User.Id));
        Assert.Empty(bobs.Outgoing);
        Assert.Equal([bob.Id], (await RequestsAsync(ada)).Outgoing.Select(r => r.User.Id));

        // Only the recipient can accept.
        Assert.Equal(HttpStatusCode.NotFound, (await ada.PostAsync($"/friends/requests/{sent.Body.Request.Id}/accept", new { })).StatusCode);
        var accepted = await bob.PostAsync($"/friends/requests/{sent.Body.Request.Id}/accept", new { });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        Assert.Equal([bob.Id], (await FriendsAsync(ada)).Select(f => f.User.Id));
        Assert.Equal([ada.Id], (await FriendsAsync(bob)).Select(f => f.User.Id));
        Assert.Empty((await RequestsAsync(bob)).Incoming);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(bob, ada.Auth.User.Username)).Status);

        Assert.Equal(HttpStatusCode.NoContent, (await ada.Client.DeleteAsync(new Uri($"/friends/{bob.Id}", UriKind.Relative))).StatusCode);
        Assert.Empty(await FriendsAsync(bob));
        // Idempotent.
        Assert.Equal(HttpStatusCode.NoContent, (await ada.Client.DeleteAsync(new Uri($"/friends/{bob.Id}", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task AskingSomeoneWhoAskedYouMakesYouFriends()
    {
        using var ada = await TestUser.RegisterAsync(factory);
        using var bob = await TestUser.RegisterAsync(factory);
        await SendAsync(ada, bob.Auth.User.Username);

        var reply = await SendAsync(bob, ada.Auth.User.Username);
        Assert.Equal((HttpStatusCode.OK, FriendRequestOutcome.Friends), (reply.Status, reply.Body!.Outcome));
        Assert.Equal(ada.Id, reply.Body.Friend!.User.Id);
        Assert.Equal([bob.Id], (await FriendsAsync(ada)).Select(f => f.User.Id));
        Assert.Empty((await RequestsAsync(ada)).Outgoing);
    }

    [Fact]
    public async Task DeclineAndCancelAreIdempotentAndInvalidTargetsAreRejected()
    {
        using var ada = await TestUser.RegisterAsync(factory);
        using var bob = await TestUser.RegisterAsync(factory);

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(ada, "no-such-user-" + Guid.NewGuid().ToString("N")[..8])).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(ada, ada.Auth.User.Username)).Status);

        var first = (await SendAsync(ada, bob.Auth.User.Username)).Body!.Request!.Id;
        Assert.Equal(HttpStatusCode.NoContent, (await bob.PostAsync($"/friends/requests/{first}/decline", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await bob.PostAsync($"/friends/requests/{first}/decline", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await bob.PostAsync($"/friends/requests/{first}/accept", new { })).StatusCode);
        Assert.Empty((await RequestsAsync(ada)).Outgoing);

        // A declined request does not stop a new one.
        var second = (await SendAsync(ada, bob.Auth.User.Username)).Body!.Request!.Id;
        Assert.NotEqual(first, second);
        var cancel = new Uri($"/friends/requests/{second}", UriKind.Relative);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.Client.DeleteAsync(cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ada.Client.DeleteAsync(cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ada.Client.DeleteAsync(cancel)).StatusCode);
        Assert.Empty((await RequestsAsync(bob)).Incoming);
    }

    [Fact]
    public async Task BlockingEndsTheFriendshipAndHidesYou()
    {
        using var ada = await TestUser.RegisterAsync(factory);
        using var bob = await TestUser.RegisterAsync(factory);
        await SendAsync(ada, bob.Auth.User.Username);
        await SendAsync(bob, ada.Auth.User.Username);
        Assert.Single(await FriendsAsync(ada));

        Assert.Equal(HttpStatusCode.NoContent, (await ada.Client.PutAsync(new Uri($"/blocks/{bob.Id}", UriKind.Relative), null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ada.Client.PutAsync(new Uri($"/blocks/{bob.Id}", UriKind.Relative), null)).StatusCode);
        Assert.Empty(await FriendsAsync(ada));
        Assert.Empty(await FriendsAsync(bob));
        Assert.Equal([bob.Id], (await BlocksAsync(ada)).Select(b => b.User.Id));
        Assert.Empty(await BlocksAsync(bob));

        // Neither side can send a request; the blocked one is told the user does not exist.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(bob, ada.Auth.User.Username)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(ada, bob.Auth.User.Username)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await ada.Client.PutAsync(new Uri($"/blocks/{ada.Id}", UriKind.Relative), null)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await ada.Client.DeleteAsync(new Uri($"/blocks/{bob.Id}", UriKind.Relative))).StatusCode);
        Assert.Empty(await BlocksAsync(ada));
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(bob, ada.Auth.User.Username)).Status);
    }

    [Fact]
    public async Task RecipientsAreToldAndFriendsSeeEachOtherOnline()
    {
        using var ada = await TestUser.RegisterAsync(factory);
        using var bob = await TestUser.RegisterAsync(factory);
        await using var bobs = await ConnectAsync(bob);
        var changes = Channel.CreateUnbounded<FriendsChangedEvent>();
        var presence = Channel.CreateUnbounded<PresenceChangedEvent>();
        bobs.On<FriendsChangedEvent>(SocialEvents.FriendsChanged, e => changes.Writer.TryWrite(e));
        bobs.On<PresenceChangedEvent>(PresenceTracker.PresenceChanged, e => presence.Writer.TryWrite(e));

        var request = (await SendAsync(ada, bob.Auth.User.Username)).Body!.Request!;
        var received = await NextAsync(changes);
        Assert.Equal((FriendsChange.RequestReceived, ada.Id), (received.Change, received.User.Id));

        await bob.PostAsync($"/friends/requests/{request.Id}/accept", new { });
        var added = await NextAsync(changes);
        Assert.Equal((FriendsChange.FriendAdded, ada.Id), (added.Change, added.User.Id));

        // No shared community: presence still reaches friends.
        Assert.Empty((await bobs.InvokeAsync<FriendsPresenceResponse>("GetFriendsPresence")).OnlineUserIds);
        var adas = await ConnectAsync(ada);
        Assert.Equal(new PresenceChangedEvent(ada.Id, true), await NextAsync(presence));
        Assert.Equal([ada.Id], (await bobs.InvokeAsync<FriendsPresenceResponse>("GetFriendsPresence")).OnlineUserIds);
        await adas.DisposeAsync();
        Assert.Equal(new PresenceChangedEvent(ada.Id, false), await NextAsync(presence));
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
            // Same wire format as the API: enums as camelCase strings.
            .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false)))
            .Build();
        await connection.StartAsync();
        return connection;
    }

    private static async Task<T> NextAsync<T>(Channel<T> events)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await events.Reader.ReadAsync(timeout.Token);
    }

    private static async Task<(HttpStatusCode Status, SendFriendRequestResponse? Body)> SendAsync(TestUser user, string username)
    {
        var response = await user.PostAsync("/friends/requests", new SendFriendRequestRequest(username));
        var body = response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<SendFriendRequestResponse>(TestJson.Options) : null;
        return (response.StatusCode, body);
    }

    private static async Task<FriendRequestListResponse> RequestsAsync(TestUser user) =>
        (await (await user.GetAsync("/friends/requests")).Content.ReadFromJsonAsync<FriendRequestListResponse>(TestJson.Options))!;

    private static async Task<List<FriendResponse>> FriendsAsync(TestUser user) =>
        (await (await user.GetAsync("/friends")).Content.ReadFromJsonAsync<List<FriendResponse>>(TestJson.Options))!;

    private static async Task<List<BlockedUserResponse>> BlocksAsync(TestUser user) =>
        (await (await user.GetAsync("/blocks")).Content.ReadFromJsonAsync<List<BlockedUserResponse>>(TestJson.Options))!;
}
