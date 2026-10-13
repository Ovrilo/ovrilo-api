using System.Net;
using System.Net.Http.Json;

using Ovrilo.Modules.Identity.Features;

namespace Ovrilo.Api.Tests.Identity;

[Collection(SharedApiFixture.Name)]
public sealed class SessionLifecycleTests(OvriloApiFactory factory)
{
    [Fact]
    public async Task RefreshRotatesTokens()
    {
        using var client = factory.CreateClient();
        var registered = await client.RegisterNewAsync();

        var response = await client.RefreshAsync(registered.RefreshToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var refreshed = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        Assert.NotEqual(registered.RefreshToken, refreshed.RefreshToken);
        Assert.Equal(registered.SessionExpiresAt, refreshed.SessionExpiresAt);
        var me = await client.SendAuthorizedAsync(HttpMethod.Get, "/users/me", refreshed.AccessToken);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task ReusingARotatedRefreshTokenRevokesTheSession()
    {
        using var client = factory.CreateClient();
        var registered = await client.RegisterNewAsync();
        var rotated = (await (await client.RefreshAsync(registered.RefreshToken)).Content.ReadFromJsonAsync<AuthResponse>())!;

        var replay = await client.RefreshAsync(registered.RefreshToken);
        var afterReplay = await client.RefreshAsync(rotated.RefreshToken);

        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal("identity.invalid_refresh_token", await replay.ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, afterReplay.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("0123456789abcdef0123456789abcdef.AAAA")]
    public async Task MalformedRefreshTokenIsRejected(string token)
    {
        using var client = factory.CreateClient();

        var response = await client.RefreshAsync(token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("identity.invalid_refresh_token", await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task LogoutRevokesOnlyTheCurrentSession()
    {
        using var client = factory.CreateClient();
        var first = await client.RegisterNewAsync();
        var second = (await (await client.LoginAsync(first.User.Username)).Content.ReadFromJsonAsync<AuthResponse>())!;

        var logout = await client.SendAuthorizedAsync(HttpMethod.Post, "/auth/logout", first.AccessToken);
        var logoutAgain = await client.SendAuthorizedAsync(HttpMethod.Post, "/auth/logout", first.AccessToken);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, logoutAgain.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.RefreshAsync(first.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.RefreshAsync(second.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task LogoutAllRevokesEverySession()
    {
        using var client = factory.CreateClient();
        var first = await client.RegisterNewAsync();
        var second = (await (await client.LoginAsync(first.User.Username)).Content.ReadFromJsonAsync<AuthResponse>())!;

        var response = await client.SendAuthorizedAsync(HttpMethod.Post, "/auth/logout-all", second.AccessToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.RefreshAsync(first.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.RefreshAsync(second.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task LogoutRequiresAuthentication()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsync(new Uri("/auth/logout", UriKind.Relative), content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
