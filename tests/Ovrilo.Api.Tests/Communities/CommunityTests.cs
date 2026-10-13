using System.Net;
using System.Net.Http.Json;

using Ovrilo.Api.Tests.Identity;
using Ovrilo.Modules.Communities.Contracts;
using Ovrilo.Modules.Communities.Features;

namespace Ovrilo.Api.Tests.Communities;

[Collection(SharedApiFixture.Name)]
public sealed class CommunityTests(OvriloApiFactory factory)
{
    [Fact]
    public async Task TwoUsersRegisterAndJoinTheSamePrivateCommunity()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        using var friend = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync("Squad");
        var invitation = await owner.CreateInvitationAsync(community.Id);

        var accept = await friend.AcceptAsync(invitation.Code);

        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        var joined = (await accept.Content.ReadFromJsonAsync<CommunityResponse>(TestJson.Options))!;
        Assert.Equal(community.Id, joined.Id);
        Assert.Equal(CommunityRole.Member, joined.MyRole);

        var friendCommunities = await friend.Client.GetFromJsonAsync<List<CommunityResponse>>(new Uri("/communities", UriKind.Relative), TestJson.Options);
        Assert.Contains(friendCommunities!, c => c.Id == community.Id);

        var members = await owner.Client.GetFromJsonAsync<List<MemberResponse>>(new Uri($"/communities/{community.Id}/members", UriKind.Relative), TestJson.Options);
        Assert.Equal([owner.Id, friend.Id], members!.Select(m => m.UserId));
        Assert.Equal(CommunityRole.Owner, members![0].Role);
        Assert.Equal(friend.Auth.User.Username, members[1].Username);
    }

    [Fact]
    public async Task RolesAreSerializedAsCamelCaseStrings()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();

        var json = await owner.Client.GetStringAsync(new Uri($"/communities/{community.Id}", UriKind.Relative));

        Assert.Contains("\"myRole\":\"owner\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonMembersCannotSeeACommunity()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        using var stranger = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();

        var get = await stranger.GetAsync($"/communities/{community.Id}");
        var members = await stranger.GetAsync($"/communities/{community.Id}/members");
        var invite = await stranger.PostAsync($"/communities/{community.Id}/invitations", new CreateInvitationRequest(null, null));

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal("communities.not_found", await get.ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.NotFound, members.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, invite.StatusCode);
    }

    [Fact]
    public async Task SingleUseInvitationCannotBeReused()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        using var first = await TestUser.RegisterAsync(factory);
        using var second = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();
        var invitation = await owner.CreateInvitationAsync(community.Id, maxUses: 1);

        var firstAccept = await first.AcceptAsync(invitation.Code);
        var repeatAccept = await first.AcceptAsync(invitation.Code);
        var secondAccept = await second.AcceptAsync(invitation.Code);

        Assert.Equal(HttpStatusCode.OK, firstAccept.StatusCode);
        Assert.Equal(HttpStatusCode.OK, repeatAccept.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, secondAccept.StatusCode);
        Assert.Equal("communities.invitation_invalid", await secondAccept.ProblemCodeAsync());
    }

    [Fact]
    public async Task ConcurrentAcceptsNeverExceedMaxUses()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();
        var invitation = await owner.CreateInvitationAsync(community.Id, maxUses: 2);
        var joiners = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => TestUser.RegisterAsync(factory)));

        var responses = await Task.WhenAll(joiners.Select(j => j.AcceptAsync(invitation.Code)));

        Assert.Equal(2, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(4, responses.Count(r => r.StatusCode == HttpStatusCode.NotFound));
        foreach (var joiner in joiners)
        {
            joiner.Dispose();
        }
    }

    [Fact]
    public async Task RevokedInvitationIsRejected()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        using var friend = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();
        var invitation = await owner.CreateInvitationAsync(community.Id, maxUses: 5);

        var revoke = await owner.Client.DeleteAsync(new Uri($"/communities/{community.Id}/invitations/{invitation.Id}", UriKind.Relative));
        var accept = await friend.AcceptAsync(invitation.Code);

        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, accept.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("AAAAAAAAAAAA")]
    public async Task UnknownInvitationCodesAreRejected(string code)
    {
        using var user = await TestUser.RegisterAsync(factory);

        var response = await user.AcceptAsync(code);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("communities.invitation_invalid", await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task MembersCanLeaveButOwnerCannot()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        using var friend = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();
        await friend.AcceptAsync((await owner.CreateInvitationAsync(community.Id)).Code);

        var ownerLeave = await owner.PostAsync<object?>($"/communities/{community.Id}/leave", null);
        var friendLeave = await friend.PostAsync<object?>($"/communities/{community.Id}/leave", null);
        var afterLeave = await friend.GetAsync($"/communities/{community.Id}");

        Assert.Equal(HttpStatusCode.Conflict, ownerLeave.StatusCode);
        Assert.Equal("communities.owner_cannot_leave", await ownerLeave.ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.NoContent, friendLeave.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, afterLeave.StatusCode);

        var rejoin = await friend.AcceptAsync((await owner.CreateInvitationAsync(community.Id)).Code);
        Assert.Equal(HttpStatusCode.OK, rejoin.StatusCode);
    }

    [Fact]
    public async Task InvalidCommunityAndInvitationInputIsRejected()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();

        var emptyName = await owner.PostAsync("/communities", new CreateCommunityRequest("   ", null));
        var tooManyUses = await owner.PostAsync($"/communities/{community.Id}/invitations", new CreateInvitationRequest(1000, null));

        Assert.Equal(HttpStatusCode.BadRequest, emptyName.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooManyUses.StatusCode);
    }

    [Fact]
    public async Task CommunityEndpointsRequireAuthentication()
    {
        using var anonymous = factory.CreateClient();

        var response = await anonymous.GetAsync(new Uri("/communities", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
