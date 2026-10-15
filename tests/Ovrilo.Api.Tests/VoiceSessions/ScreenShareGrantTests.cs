using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

using Ovrilo.Api.Tests.Communities;
using Ovrilo.Api.Tests.Identity;
using Ovrilo.Modules.VoiceSessions.Features;

namespace Ovrilo.Api.Tests.VoiceSessions;

/// <summary>Screen-share grants (ADR-0017) behind the <c>VoiceSessions:ScreenSharing:Enabled</c> flag.</summary>
[Collection(SharedApiFixture.Name)]
public sealed class ScreenShareGrantTests(OvriloApiFactory factory)
{
    private WebApplicationFactory<Program> Enabled => factory.WithWebHostBuilder(builder =>
        builder.UseSetting("VoiceSessions:ScreenSharing:Enabled", "true"));

    [Fact]
    public async Task WhileTheFlagIsOffGrantsStayMicrophoneOnlyAndWatchingIsRefused()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();
        var voice = await owner.DefaultChannelAsync(community.Id, "voice");

        var join = await ReadAsync<VoiceJoinResponse>(await owner.PostAsync($"/channels/{voice.Id}/voice/join", new { }));
        var watch = await owner.PostAsync($"/channels/{voice.Id}/voice/watch", new { });

        Assert.False(join.CanShareScreen);
        Assert.Equal(["microphone"], PublishSources(await VideoGrantAsync(join.Token)));
        Assert.Equal(HttpStatusCode.Forbidden, watch.StatusCode);
        Assert.Equal("voice.screen_share_disabled", await watch.ProblemCodeAsync());
    }

    [Fact]
    public async Task MembersMayPublishTheirScreenAndItsSound()
    {
        var app = Enabled;
        using var owner = await TestUser.RegisterAsync(app);
        using var member = await TestUser.RegisterAsync(app);
        var community = await owner.CreateCommunityAsync();
        await member.AcceptAsync((await owner.CreateInvitationAsync(community.Id)).Code);
        var voice = await owner.DefaultChannelAsync(community.Id, "voice");

        var join = await ReadAsync<VoiceJoinResponse>(await member.PostAsync($"/channels/{voice.Id}/voice/join", new { }));

        Assert.True(join.CanShareScreen);
        Assert.Equal(["microphone", "screen_share", "screen_share_audio"], PublishSources(await VideoGrantAsync(join.Token)));
    }

    [Fact]
    public async Task ViewersGetAHiddenSubscribeOnlyIdentity()
    {
        var app = Enabled;
        using var owner = await TestUser.RegisterAsync(app);
        var community = await owner.CreateCommunityAsync();
        var voice = await owner.DefaultChannelAsync(community.Id, "voice");

        var watch = await ReadAsync<VoiceWatchResponse>(await owner.PostAsync($"/channels/{voice.Id}/voice/watch", new { }));
        var (subject, grants) = await ValidateAsync(watch.Token);

        Assert.Equal(OvriloApiFactory.TestLiveKitUrl, watch.ServerUrl);
        Assert.Equal($"{owner.Id}#viewer", subject);
        Assert.Equal($"channel_{voice.Id:N}", grants.GetProperty("room").GetString());
        Assert.True(grants.GetProperty("roomJoin").GetBoolean());
        Assert.True(grants.GetProperty("hidden").GetBoolean());
        Assert.True(grants.GetProperty("canSubscribe").GetBoolean());
        Assert.False(grants.GetProperty("canPublish").GetBoolean());
        Assert.False(grants.GetProperty("canPublishData").GetBoolean());
        Assert.False(grants.TryGetProperty("canPublishSources", out _));
    }

    [Fact]
    public async Task WatchingNeedsTheRightsToJoin()
    {
        var app = Enabled;
        using var owner = await TestUser.RegisterAsync(app);
        using var stranger = await TestUser.RegisterAsync(app);
        var community = await owner.CreateCommunityAsync();
        var voice = await owner.DefaultChannelAsync(community.Id, "voice");
        var text = await owner.DefaultChannelAsync(community.Id, "text");

        var outsider = await stranger.PostAsync($"/channels/{voice.Id}/voice/watch", new { });
        var textChannel = await owner.PostAsync($"/channels/{text.Id}/voice/watch", new { });

        Assert.Equal(HttpStatusCode.NotFound, outsider.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, textChannel.StatusCode);
        Assert.Equal("voice.permission_denied", await textChannel.ProblemCodeAsync());
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(TestJson.Options))!;
    }

    private static List<string?> PublishSources(JsonElement grants) =>
        grants.GetProperty("canPublishSources").EnumerateArray().Select(s => s.GetString()).ToList();

    private static async Task<JsonElement> VideoGrantAsync(string token) => (await ValidateAsync(token)).Grants;

    private static async Task<(string Subject, JsonElement Grants)> ValidateAsync(string token)
    {
        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = OvriloApiFactory.TestLiveKitKey,
            ValidateAudience = false,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(OvriloApiFactory.TestLiveKitSecret)),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        });
        Assert.True(validation.IsValid, validation.Exception?.Message);
        var jwt = (JsonWebToken)validation.SecurityToken;
        using var video = JsonDocument.Parse(jwt.GetClaim("video").Value);
        return (jwt.Subject, video.RootElement.Clone());
    }
}
