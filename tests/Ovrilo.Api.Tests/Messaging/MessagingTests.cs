using System.Net;
using System.Net.Http.Json;

using Ovrilo.Api.Tests.Communities;
using Ovrilo.Api.Tests.Identity;
using Ovrilo.Modules.Channels.Features;
using Ovrilo.Modules.Messaging.Features;

namespace Ovrilo.Api.Tests.Messaging;

[Collection(SharedApiFixture.Name)]
public sealed class MessagingTests(OvriloApiFactory factory)
{
    private sealed record Room(TestUser Owner, TestUser Member, Guid CommunityId, ChannelResponse General) : IDisposable
    {
        public void Dispose()
        {
            Owner.Dispose();
            Member.Dispose();
        }
    }

    private async Task<Room> CreateRoomAsync()
    {
        var owner = await TestUser.RegisterAsync(factory);
        var member = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();
        await member.AcceptAsync((await owner.CreateInvitationAsync(community.Id)).Code);
        var general = await owner.DefaultChannelAsync(community.Id, "text");
        return new Room(owner, member, community.Id, general);
    }

    [Fact]
    public async Task MembersPostAndReadMessagesWithAuthorNames()
    {
        using var room = await CreateRoomAsync();

        var first = await SendAsync(room.Owner, room.General.Id, "  hello  ");
        var second = await SendAsync(room.Member, room.General.Id, "hi!\nline two");

        Assert.Equal("hello", first.Content);
        Assert.True(second.Id > first.Id);
        var page = await ListAsync(room.Member, room.General.Id);
        Assert.Equal([first.Id, second.Id], page.Messages.Select(m => m.Id));
        Assert.False(page.HasMore);
        Assert.Equal(room.Member.Auth.User.DisplayName, page.Messages[1].Author.DisplayName);
        Assert.Equal("hi!\nline two", page.Messages[1].Content);
    }

    [Fact]
    public async Task ResendingTheSameClientIdReturnsTheOriginalMessage()
    {
        using var room = await CreateRoomAsync();
        var clientId = Guid.NewGuid();

        var created = await room.Member.PostAsync($"/channels/{room.General.Id}/messages", new SendMessageRequest(clientId, "once", null));
        var retried = await room.Member.PostAsync($"/channels/{room.General.Id}/messages", new SendMessageRequest(clientId, "once (retry)", null));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        var original = await created.Content.ReadFromJsonAsync<MessageResponse>(TestJson.Options);
        var replay = await retried.Content.ReadFromJsonAsync<MessageResponse>(TestJson.Options);
        Assert.Equal(original!.Id, replay!.Id);
        Assert.Equal("once", replay.Content);
        Assert.Single((await ListAsync(room.Member, room.General.Id)).Messages);

        // Another user may use the same client ID: deduplication is per author.
        var other = await room.Owner.PostAsync($"/channels/{room.General.Id}/messages", new SendMessageRequest(clientId, "mine", null));
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);

        // Reusing it in another channel is a client bug, not a retry.
        var clips = await room.Owner.CreateChannelAsync(room.CommunityId, "clips", "text");
        var conflict = await room.Member.PostAsync($"/channels/{clips.Id}/messages", new SendMessageRequest(clientId, "elsewhere", null));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("messaging.client_id_conflict", await conflict.ProblemCodeAsync());
    }

    [Fact]
    public async Task StrangersCannotReadOrPostAndVoiceChannelsHaveNoMessages()
    {
        using var room = await CreateRoomAsync();
        using var stranger = await TestUser.RegisterAsync(factory);
        var lobby = await room.Owner.CreateChannelAsync(room.CommunityId, "Lobby", "voice");

        var read = await stranger.GetAsync($"/channels/{room.General.Id}/messages");
        var post = await stranger.PostAsync($"/channels/{room.General.Id}/messages", new SendMessageRequest(Guid.NewGuid(), "hey", null));
        var voice = await room.Member.PostAsync($"/channels/{lobby.Id}/messages", new SendMessageRequest(Guid.NewGuid(), "hey", null));

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        Assert.Equal("channels.not_found", await post.ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.Forbidden, voice.StatusCode);
        Assert.Equal("messaging.permission_denied", await voice.ProblemCodeAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData("bell\u0007")]
    public async Task InvalidContentIsRejected(string content)
    {
        using var room = await CreateRoomAsync();

        var response = await room.Member.PostAsync($"/channels/{room.General.Id}/messages", new SendMessageRequest(Guid.NewGuid(), content, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task OversizedMessagesAreRejected()
    {
        using var room = await CreateRoomAsync();

        var response = await room.Member.PostAsync(
            $"/channels/{room.General.Id}/messages",
            new SendMessageRequest(Guid.NewGuid(), new string('a', 4001), null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task HistoryPagesBackwardAndCatchesUpForward()
    {
        using var room = await CreateRoomAsync();
        var ids = new List<long>();
        for (var i = 0; i < 7; i++)
        {
            ids.Add((await SendAsync(room.Owner, room.General.Id, $"message {i}")).Id);
        }

        var newest = await ListAsync(room.Member, room.General.Id, "limit=3");
        Assert.Equal(ids[4..], newest.Messages.Select(m => m.Id));
        Assert.True(newest.HasMore);

        var older = await ListAsync(room.Member, room.General.Id, $"limit=3&before={ids[4]}");
        Assert.Equal(ids[1..4], older.Messages.Select(m => m.Id));
        Assert.True(older.HasMore);

        var oldest = await ListAsync(room.Member, room.General.Id, $"limit=3&before={ids[1]}");
        Assert.Equal([ids[0]], oldest.Messages.Select(m => m.Id));
        Assert.False(oldest.HasMore);

        var missed = await ListAsync(room.Member, room.General.Id, $"limit=2&after={ids[2]}");
        Assert.Equal(ids[3..5], missed.Messages.Select(m => m.Id));
        Assert.True(missed.HasMore);

        var bad = await room.Member.GetAsync($"/channels/{room.General.Id}/messages?before=5&after=1");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task AuthorsEditTheirOwnMessagesOnly()
    {
        using var room = await CreateRoomAsync();
        var message = await SendAsync(room.Member, room.General.Id, "typo");

        var edited = await room.Member.Client.PatchAsJsonAsync(new Uri($"/messages/{message.Id}", UriKind.Relative), new EditMessageRequest("fixed"));
        var hijack = await room.Owner.Client.PatchAsJsonAsync(new Uri($"/messages/{message.Id}", UriKind.Relative), new EditMessageRequest("owned"));

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var body = await edited.Content.ReadFromJsonAsync<MessageResponse>(TestJson.Options);
        Assert.Equal("fixed", body!.Content);
        Assert.NotNull(body.EditedAt);
        Assert.Equal(HttpStatusCode.Forbidden, hijack.StatusCode);
    }

    [Fact]
    public async Task DeletesLeaveTombstonesAndOnlyAuthorsOrModeratorsMayDelete()
    {
        using var room = await CreateRoomAsync();
        var mine = await SendAsync(room.Member, room.General.Id, "mine");
        var owners = await SendAsync(room.Owner, room.General.Id, "owner's");
        var moderated = await SendAsync(room.Member, room.General.Id, "rude");

        var ownDelete = await room.Member.Client.DeleteAsync(new Uri($"/messages/{mine.Id}", UriKind.Relative));
        var again = await room.Member.Client.DeleteAsync(new Uri($"/messages/{mine.Id}", UriKind.Relative));
        var notAllowed = await room.Member.Client.DeleteAsync(new Uri($"/messages/{owners.Id}", UriKind.Relative));
        var moderation = await room.Owner.Client.DeleteAsync(new Uri($"/messages/{moderated.Id}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NoContent, ownDelete.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, notAllowed.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, moderation.StatusCode);

        var page = await ListAsync(room.Member, room.General.Id);
        var tombstone = page.Messages.Single(m => m.Id == mine.Id);
        Assert.Null(tombstone.Content);
        Assert.NotNull(tombstone.DeletedAt);
        Assert.Null(page.Messages.Single(m => m.Id == moderated.Id).Content);
        Assert.Equal("owner's", page.Messages.Single(m => m.Id == owners.Id).Content);

        var editDeleted = await room.Member.Client.PatchAsJsonAsync(new Uri($"/messages/{mine.Id}", UriKind.Relative), new EditMessageRequest("back"));
        Assert.Equal(HttpStatusCode.NotFound, editDeleted.StatusCode);
    }

    [Fact]
    public async Task RepliesMustStayInTheSameChannel()
    {
        using var room = await CreateRoomAsync();
        var clips = await room.Owner.CreateChannelAsync(room.CommunityId, "clips", "text");
        var elsewhere = await SendAsync(room.Owner, clips.Id, "over here");
        var target = await SendAsync(room.Owner, room.General.Id, "question?");

        var reply = await room.Member.PostAsync($"/channels/{room.General.Id}/messages", new SendMessageRequest(Guid.NewGuid(), "answer", target.Id));
        var crossChannel = await room.Member.PostAsync($"/channels/{room.General.Id}/messages", new SendMessageRequest(Guid.NewGuid(), "answer", elsewhere.Id));

        Assert.Equal(HttpStatusCode.Created, reply.StatusCode);
        Assert.Equal(target.Id, (await reply.Content.ReadFromJsonAsync<MessageResponse>(TestJson.Options))!.ReplyToMessageId);
        Assert.Equal(HttpStatusCode.BadRequest, crossChannel.StatusCode);
    }

    private static async Task<MessageResponse> SendAsync(TestUser user, Guid channelId, string content)
    {
        var response = await user.PostAsync($"/channels/{channelId}/messages", new SendMessageRequest(Guid.NewGuid(), content, null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MessageResponse>(TestJson.Options))!;
    }

    private static async Task<MessagePageResponse> ListAsync(TestUser user, Guid channelId, string query = "")
    {
        var response = await user.GetAsync($"/channels/{channelId}/messages{(query.Length > 0 ? "?" + query : "")}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MessagePageResponse>(TestJson.Options))!;
    }
}
