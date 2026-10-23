using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Ovrilo.Modules.Identity.Features;

namespace Ovrilo.Api.Tests.Identity;

/// <summary>The browser client's refresh-cookie transport (X-Ovrilo-Session: cookie).</summary>
[Collection(SharedApiFixture.Name)]
public sealed class RefreshCookieTests(OvriloApiFactory factory)
{
    private const string CookieName = "__Secure-ovrilo-refresh";

    [Fact]
    public async Task LoginWithCookieTransportSetsAnHttpOnlyCookieInsteadOfReturningTheToken()
    {
        using var client = CreateClient();
        var registered = await client.RegisterNewAsync();

        var response = await client.SendAsync(CookieRequest("/auth/login", new LoginRequest(registered.User.Username, IdentityTestClient.Password, "Browser")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options))!;
        Assert.Null(body.RefreshToken);
        var setCookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(CookieName + "=", StringComparison.Ordinal)).ToLowerInvariant();
        Assert.Contains("httponly", setCookie);
        Assert.Contains("secure", setCookie);
        Assert.Contains("samesite=strict", setCookie);
        Assert.Contains("path=/auth", setCookie);
        Assert.Contains("expires=", setCookie);
    }

    [Fact]
    public async Task RegisterWithCookieTransportSetsTheCookie()
    {
        using var client = CreateClient();
        var username = IdentityTestClient.UniqueUsername();

        var response = await client.SendAsync(CookieRequest(
            "/auth/register",
            new RegisterRequest(username, "Web User", $"{username}@example.com", IdentityTestClient.Password, "Browser")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Null((await response.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options))!.RefreshToken);
        Assert.NotNull(CookieFrom(response));
    }

    [Fact]
    public async Task RefreshReadsAndRotatesTheCookie()
    {
        using var client = CreateClient();
        var first = await SignInWithCookieAsync(client);

        var response = await client.SendAsync(CookieRequest("/auth/refresh", new RefreshRequest(null), first));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options))!;
        Assert.Null(body.RefreshToken);
        var rotated = CookieFrom(response);
        Assert.NotNull(rotated);
        Assert.NotEqual(first, rotated);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAuthorizedAsync(HttpMethod.Get, "/users/me", body.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task ReplayingARotatedCookieRevokesTheSession()
    {
        using var client = CreateClient();
        var first = await SignInWithCookieAsync(client);
        var rotated = CookieFrom(await client.SendAsync(CookieRequest("/auth/refresh", new RefreshRequest(null), first)))!;

        var replay = await client.SendAsync(CookieRequest("/auth/refresh", new RefreshRequest(null), first));
        var afterReplay = await client.SendAsync(CookieRequest("/auth/refresh", new RefreshRequest(null), rotated));

        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, afterReplay.StatusCode);
    }

    [Fact]
    public async Task CookieTransportIgnoresATokenInTheBody()
    {
        using var client = CreateClient();
        var registered = await client.RegisterNewAsync();

        // Without the cookie, a body token is not accepted in cookie mode.
        var response = await client.SendAsync(CookieRequest("/auth/refresh", new RefreshRequest(registered.RefreshToken)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("identity.invalid_refresh_token", await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task WithoutTheHeaderTheCookieIsIgnored()
    {
        using var client = CreateClient();
        var cookie = await SignInWithCookieAsync(client);
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/auth/refresh", UriKind.Relative))
        {
            Content = JsonContent.Create(new RefreshRequest(null)),
        };
        request.Headers.Add("Cookie", $"{CookieName}={cookie}");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task LogoutClearsTheCookieAndRevokesTheSession()
    {
        using var client = CreateClient();
        var registered = await client.RegisterNewAsync();
        var login = await client.SendAsync(CookieRequest("/auth/login", new LoginRequest(registered.User.Username, IdentityTestClient.Password, "Browser")));
        var cookie = CookieFrom(login)!;
        var access = (await login.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options))!.AccessToken;

        var logout = CookieRequest("/auth/logout", body: null, cookie);
        logout.Headers.Authorization = new("Bearer", access);
        var response = await client.SendAsync(logout);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var cleared = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(CookieName + "=", StringComparison.Ordinal));
        Assert.StartsWith(CookieName + "=;", cleared, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(CookieRequest("/auth/refresh", new RefreshRequest(null), cookie))).StatusCode);
    }

    [Fact]
    public async Task DesktopResponsesStillCarryTheTokenAndSetNoCookie()
    {
        using var client = CreateClient();

        var response = await client.RegisterAsync(IdentityTestClient.UniqueUsername());

        Assert.NotNull((await response.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options))!.RefreshToken);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    // Cookies are handled by hand: the test server is plain HTTP and the cookie is Secure.
    private HttpClient CreateClient() => factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    private static async Task<string> SignInWithCookieAsync(HttpClient client)
    {
        var registered = await client.RegisterNewAsync();
        var response = await client.SendAsync(CookieRequest("/auth/login", new LoginRequest(registered.User.Username, IdentityTestClient.Password, "Browser")));
        response.EnsureSuccessStatusCode();
        return CookieFrom(response)!;
    }

    private static HttpRequestMessage CookieRequest(string path, object? body, string? cookie = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        request.Headers.Add("X-Ovrilo-Session", "cookie");
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", $"{CookieName}={cookie}");
        }

        return request;
    }

    private static string? CookieFrom(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return null;
        }

        var cookie = values.FirstOrDefault(c => c.StartsWith(CookieName + "=", StringComparison.Ordinal));
        if (cookie is null)
        {
            return null;
        }

        var value = cookie[(CookieName.Length + 1)..].Split(';')[0];
        return value.Length == 0 ? null : Uri.UnescapeDataString(value);
    }
}
