using System.Net;
using System.Net.Http.Json;

using Ovrilo.Api.Tests.Communities;
using Ovrilo.Modules.Communities.Features;
using Ovrilo.Modules.Identity.Features;
using Ovrilo.Modules.Messaging.Features;
using Ovrilo.Modules.Social.Features;

namespace Ovrilo.Api.Tests.Identity;

[Collection(SharedApiFixture.Name)]
public sealed class AccountDeletionTests(OvriloApiFactory factory)
{
    private static Task<HttpResponseMessage> DeleteAsync(TestUser user, string password = IdentityTestClient.Password) =>
        user.PostAsync("/users/me/delete", new DeleteAccountRequest(password));

    private static async Task MakeFriendsAsync(TestUser a, TestUser b)
    {
        await a.PostAsync("/friends/requests", new SendFriendRequestRequest(b.Auth.User.Username));
        await b.PostAsync("/friends/requests", new SendFriendRequestRequest(a.Auth.User.Username));
    }

    [Fact]
    public async Task WrongPasswordAndOwnedCommunitiesStopIt()
    {
        using var ada = await TestUser.RegisterAsync(factory);
        Assert.Equal(HttpStatusCode.BadRequest, (await DeleteAsync(ada, "not my password")).StatusCode);

        var community = await ada.CreateCommunityAsync();
        var blocked = await DeleteAsync(ada);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal("identity.account_delete_blocked", await blocked.ProblemCodeAsync());

        // Once the community is gone, so can the account be.
        await ada.Client.DeleteAsync(new Uri($"/communities/{community.Id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(ada)).StatusCode);
    }

    [Fact]
    public async Task DeletingRemovesTheAccountEverywhereButKeepsMessages()
    {
        using var ada = await TestUser.RegisterAsync(factory);
        using var bob = await TestUser.RegisterAsync(factory);
        using var cid = await TestUser.RegisterAsync(factory);
        await MakeFriendsAsync(ada, bob);
        await MakeFriendsAsync(ada, cid);
        var community = await bob.CreateCommunityAsync();
        await ada.AcceptAsync((await bob.CreateInvitationAsync(community.Id)).Code);
        var created = await ada.PostAsync("/groups", new CreateGroupRequest([bob.Id, cid.Id], "crew"));
        var group = (await created.Content.ReadFromJsonAsync<GroupConversationResponse>(TestJson.Options))!;
        await ada.PostAsync($"/channels/{group.Id}/messages", new SendMessageRequest(Guid.NewGuid(), "bye all", null));
        var username = ada.Auth.User.Username;

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(ada)).StatusCode);

        // Signed out everywhere and unable to sign in again.
        Assert.Equal(HttpStatusCode.Unauthorized, (await ada.Client.RefreshAsync(ada.Auth.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().LoginAsync(username)).StatusCode);

        // Gone from friends, communities and groups; the group has a new owner.
        var friends = await bob.Client.GetFromJsonAsync<List<FriendResponse>>(new Uri("/friends", UriKind.Relative), TestJson.Options);
        Assert.DoesNotContain(friends!, f => f.User.Id == ada.Id);
        var members = await bob.Client.GetFromJsonAsync<List<MemberResponse>>(new Uri($"/communities/{community.Id}/members", UriKind.Relative), TestJson.Options);
        Assert.DoesNotContain(members!, m => m.UserId == ada.Id);
        var groups = await bob.Client.GetFromJsonAsync<List<GroupConversationResponse>>(new Uri("/groups", UriKind.Relative), TestJson.Options);
        var crew = Assert.Single(groups!);
        Assert.Equal(bob.Id, crew.OwnerId);
        Assert.DoesNotContain(crew.Members, m => m.Id == ada.Id);

        // The message stays, without a name; the username can be taken again.
        var page = await bob.Client.GetFromJsonAsync<MessagePageResponse>(new Uri($"/channels/{group.Id}/messages", UriKind.Relative), TestJson.Options);
        var message = Assert.Single(page!.Messages);
        Assert.Equal(("bye all", (string?)null), (message.Content, message.Author.DisplayName));
        Assert.Equal(HttpStatusCode.Created, (await factory.CreateClient().RegisterAsync(username)).StatusCode);
    }
}
