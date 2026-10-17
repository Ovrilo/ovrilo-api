using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

using Ovrilo.Api.Tests.Communities;
using Ovrilo.Api.Tests.Identity;
using Ovrilo.Modules.Messaging.Features;
using Ovrilo.Modules.Social.Features;

namespace Ovrilo.Api.Tests.Messaging;

[Collection(SharedApiFixture.Name)]
public sealed class GroupTests(OvriloApiFactory factory)
{
    private static async Task MakeFriendsAsync(TestUser a, TestUser b)
    {
        await a.PostAsync("/friends/requests", new SendFriendRequestRequest(b.Auth.User.Username));
        var reply = await b.PostAsync("/friends/requests", new SendFriendRequestRequest(a.Auth.User.Username));
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
    }

    private static async Task<(HttpStatusCode Status, GroupConversationResponse? Body)> CreateAsync(TestUser user, string? name, params Guid[] userIds)
    {
        var response = await user.PostAsync("/groups", new CreateGroupRequest(userIds, name));
        return (response.StatusCode, response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<GroupConversationResponse>(TestJson.Options) : null);
    }

    private static Task<HttpResponseMessage> SendAsync(TestUser user, Guid conversationId, string text) =>
        user.PostAsync($"/channels/{conversationId}/messages", new SendMessageRequest(Guid.NewGuid(), text, null));

    private static async Task<List<GroupConversationResponse>> ListAsync(TestUser user) =>
        (await (await user.GetAsync("/groups")).Content.ReadFromJsonAsync<List<GroupConversationResponse>>(TestJson.Options))!;

    private static Task<HttpResponseMessage> RemoveAsync(TestUser user, Guid groupId, Guid userId) =>
        user.Client.DeleteAsync(new Uri($"/groups/{groupId}/members/{userId}", UriKind.Relative));

    [Fact]
    public async Task FriendsTalkTogetherAndEveryMemberSeesUnread()
    {
        // Ada registers last, so her ID sorts after Bob's and Cid's: she is listed first only because she created the group.
        using var bob = await TestUser.RegisterAsync(factory);
        using var cid = await TestUser.RegisterAsync(factory);
        using var ada = await TestUser.RegisterAsync(factory);
        using var eve = await TestUser.RegisterAsync(factory);
        await MakeFriendsAsync(ada, bob);
        await MakeFriendsAsync(ada, cid);

        var (status, group) = await CreateAsync(ada, "  Raid night ", bob.Id, cid.Id, ada.Id);
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(("Raid night", ada.Id), (group!.Name, group.OwnerId));
        // The creator first, then the others.
        Assert.Equal(ada.Id, group.Members[0].Id);
        Assert.Equal(new[] { bob.Id, cid.Id }.Order(), group.Members.Skip(1).Select(m => m.Id).Order());

        // Bob and Cid are not friends, but both write in the group.
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(bob, group.Id, "hi all")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(cid, group.Id, "hey bob")).StatusCode);
        Assert.Equal(2, Assert.Single(await ListAsync(ada)).UnreadCount);
        Assert.Equal(1, Assert.Single(await ListAsync(bob)).UnreadCount);

        // Outsiders see nothing, and nobody moderates other people's messages.
        Assert.Equal(HttpStatusCode.NotFound, (await eve.GetAsync($"/channels/{group.Id}/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(eve, group.Id, "let me in")).StatusCode);
        Assert.Empty(await ListAsync(eve));
        var page = await (await ada.GetAsync($"/channels/{group.Id}/messages")).Content.ReadFromJsonAsync<MessagePageResponse>(TestJson.Options);
        Assert.Equal(HttpStatusCode.Forbidden, (await ada.Client.DeleteAsync(new Uri($"/messages/{page!.Messages[0].Id}", UriKind.Relative))).StatusCode);

        // Any member renames; an empty name clears it.
        var renamed = await bob.Client.PatchAsJsonAsync(new Uri($"/groups/{group.Id}", UriKind.Relative), new RenameGroupRequest(" "));
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Null(Assert.Single(await ListAsync(cid)).Name);
    }

    [Fact]
    public async Task OnlyFriendsCanBeAddedAndGroupsStaySmall()
    {
        using var ada = await TestUser.RegisterAsync(factory);
        using var bob = await TestUser.RegisterAsync(factory);
        using var cid = await TestUser.RegisterAsync(factory);
        await MakeFriendsAsync(ada, bob);

        Assert.Equal(HttpStatusCode.Forbidden, (await CreateAsync(ada, null, bob.Id, cid.Id)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateAsync(ada, null, ada.Id)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateAsync(ada, new string('x', 101), bob.Id)).Status);

        var group = (await CreateAsync(ada, null, bob.Id)).Body!;
        await SendAsync(ada, group.Id, "before cid");
        // Bob is not Cid's friend.
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.PostAsync($"/groups/{group.Id}/members", new AddGroupMemberRequest(cid.Id))).StatusCode);
        await MakeFriendsAsync(bob, cid);
        Assert.Equal(HttpStatusCode.OK, (await bob.PostAsync($"/groups/{group.Id}/members", new AddGroupMemberRequest(cid.Id))).StatusCode);
        // Idempotent.
        Assert.Equal(HttpStatusCode.OK, (await bob.PostAsync($"/groups/{group.Id}/members", new AddGroupMemberRequest(cid.Id))).StatusCode);

        // Cid reads the earlier history, which does not count as unread.
        var cids = Assert.Single(await ListAsync(cid));
        Assert.Equal((3, 0), (cids.Members.Count, cids.UnreadCount));
        Assert.Equal(HttpStatusCode.OK, (await cid.GetAsync($"/channels/{group.Id}/messages")).StatusCode);

        // Ten people at most.
        var others = new List<TestUser>();
        try
        {
            for (var i = 0; i < 7; i++)
            {
                var friend = await TestUser.RegisterAsync(factory);
                others.Add(friend);
                await MakeFriendsAsync(ada, friend);
                Assert.Equal(HttpStatusCode.OK, (await ada.PostAsync($"/groups/{group.Id}/members", new AddGroupMemberRequest(friend.Id))).StatusCode);
            }

            using var eleventh = await TestUser.RegisterAsync(factory);
            await MakeFriendsAsync(ada, eleventh);
            Assert.Equal(HttpStatusCode.Conflict, (await ada.PostAsync($"/groups/{group.Id}/members", new AddGroupMemberRequest(eleventh.Id))).StatusCode);
        }
        finally
        {
            others.ForEach(o => o.Dispose());
        }
    }

    [Fact]
    public async Task MembersLeaveTheOwnerRemovesAndOwnershipPassesOn()
    {
        using var ada = await TestUser.RegisterAsync(factory);
        using var bob = await TestUser.RegisterAsync(factory);
        using var cid = await TestUser.RegisterAsync(factory);
        await MakeFriendsAsync(ada, bob);
        await MakeFriendsAsync(ada, cid);
        var group = (await CreateAsync(ada, "crew", bob.Id, cid.Id)).Body!;

        await using var bobEvents = await ListenAsync(bob);
        Assert.Equal(HttpStatusCode.Forbidden, (await RemoveAsync(bob, group.Id, cid.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await RemoveAsync(ada, group.Id, cid.Id)).StatusCode);
        Assert.Equal((GroupChange.Updated, group.Id), await bobEvents.NextAsync());
        Assert.Empty(await ListAsync(cid));
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(cid, group.Id, "still here?")).StatusCode);

        // The owner leaves: Bob, the oldest remaining member, owns the group.
        Assert.Equal(HttpStatusCode.NoContent, (await RemoveAsync(ada, group.Id, ada.Id)).StatusCode);
        var bobs = Assert.Single(await ListAsync(bob));
        Assert.Equal((bob.Id, 1), (bobs.OwnerId, bobs.Members.Count));
        Assert.Equal(HttpStatusCode.NotFound, (await RemoveAsync(ada, group.Id, ada.Id)).StatusCode);
    }

    private async Task<GroupListener> ListenAsync(TestUser user)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "hubs/realtime"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(user.Auth.AccessToken);
            })
            .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false)))
            .Build();
        var listener = new GroupListener(connection);
        await connection.StartAsync();
        return listener;
    }

    private sealed class GroupListener : IAsyncDisposable
    {
        private readonly HubConnection _connection;
        private readonly Channel<GroupChangedEvent> _events = Channel.CreateUnbounded<GroupChangedEvent>();

        public GroupListener(HubConnection connection)
        {
            _connection = connection;
            connection.On<GroupChangedEvent>(GroupEvents.GroupChanged, e => _events.Writer.TryWrite(e));
        }

        public async Task<(GroupChange, Guid)> NextAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var e = await _events.Reader.ReadAsync(timeout.Token);
            return (e.Change, e.GroupId);
        }

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }
}
