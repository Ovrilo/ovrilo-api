using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

using Ovrilo.Api.Tests.Communities;
using Ovrilo.Api.Tests.Identity;
using Ovrilo.Modules.VoiceSessions.Features;

namespace Ovrilo.Api.Tests.VoiceSessions;

[Collection(SharedApiFixture.Name)]
public sealed class VoiceJoinTests(OvriloApiFactory factory)
{
    [Fact]
    public async Task MemberGetsGrantScopedToTheChannelRoom()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        using var friend = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();
        await friend.AcceptAsync((await owner.CreateInvitationAsync(community.Id)).Code);
        var lobby = await owner.CreateChannelAsync(community.Id, "Lobby", "voice");

        var response = await friend.PostAsync($"/channels/{lobby.Id}/voice/join", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var grant = (await response.Content.ReadFromJsonAsync<VoiceJoinResponse>(TestJson.Options))!;
        Assert.Equal(OvriloApiFactory.TestLiveKitUrl, grant.ServerUrl);
        Assert.InRange(grant.ExpiresAt, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(11));

        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(grant.Token, new TokenValidationParameters
        {
            ValidIssuer = OvriloApiFactory.TestLiveKitKey,
            ValidateAudience = false,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(OvriloApiFactory.TestLiveKitSecret)),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        });
        Assert.True(validation.IsValid, validation.Exception?.Message);

        var token = (JsonWebToken)validation.SecurityToken;
        Assert.Equal(friend.Id.ToString(), token.Subject);
        Assert.Equal(friend.Auth.User.DisplayName, token.GetClaim("name").Value);

        using var video = JsonDocument.Parse(token.GetClaim("video").Value);
        var grants = video.RootElement;
        Assert.Equal($"channel_{lobby.Id:N}", grants.GetProperty("room").GetString());
        Assert.True(grants.GetProperty("roomJoin").GetBoolean());
        Assert.True(grants.GetProperty("canPublish").GetBoolean());
        Assert.True(grants.GetProperty("canSubscribe").GetBoolean());
        Assert.False(grants.GetProperty("canPublishData").GetBoolean());
        Assert.Equal(["microphone"], grants.GetProperty("canPublishSources").EnumerateArray().Select(s => s.GetString()));
        Assert.False(grants.TryGetProperty("roomAdmin", out _));
    }

    [Fact]
    public async Task NonMembersCannotJoinOrDiscoverTheChannel()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        using var stranger = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();
        var lobby = await owner.CreateChannelAsync(community.Id, "Lobby", "voice");

        var response = await stranger.PostAsync($"/channels/{lobby.Id}/voice/join", new { });
        var missing = await stranger.PostAsync($"/channels/{Guid.NewGuid()}/voice/join", new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("channels.not_found", await response.ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task TextChannelsHaveNoVoiceRoom()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();
        var general = await owner.DefaultChannelAsync(community.Id, "text");

        var response = await owner.PostAsync($"/channels/{general.Id}/voice/join", new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("voice.permission_denied", await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task DeletedChannelsCannotBeJoined()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();
        var lobby = await owner.CreateChannelAsync(community.Id, "Lobby", "voice");
        (await owner.Client.DeleteAsync(new Uri($"/channels/{lobby.Id}", UriKind.Relative))).EnsureSuccessStatusCode();

        var response = await owner.PostAsync($"/channels/{lobby.Id}/voice/join", new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AnonymousCallersAreRejected()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsync(new Uri($"/channels/{Guid.NewGuid()}/voice/join", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
