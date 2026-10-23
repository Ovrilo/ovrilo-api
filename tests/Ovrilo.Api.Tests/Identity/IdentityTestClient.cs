using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Ovrilo.Modules.Identity.Features;

namespace Ovrilo.Api.Tests.Identity;

internal static class IdentityTestClient
{
    public const string Password = "correct horse battery";

    public static string UniqueUsername(string prefix = "user") => $"{prefix}_{Guid.NewGuid():N}"[..24];

    public static Task<HttpResponseMessage> RegisterAsync(this HttpClient client, string username, string? email = null, string password = Password) =>
        client.PostAsJsonAsync(
            new Uri("/auth/register", UriKind.Relative),
            new RegisterRequest(username, "Test User", email ?? $"{username}@example.com", password, "Integration test"));

    public static async Task<AuthResponse> RegisterNewAsync(this HttpClient client, string? username = null)
    {
        var response = await client.RegisterAsync(username ?? UniqueUsername());
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options))!;
    }

    public static Task<HttpResponseMessage> LoginAsync(this HttpClient client, string login, string password = Password) =>
        client.PostAsJsonAsync(new Uri("/auth/login", UriKind.Relative), new LoginRequest(login, password, "Second device"));

    public static Task<HttpResponseMessage> RefreshAsync(this HttpClient client, string? refreshToken) =>
        client.PostAsJsonAsync(new Uri("/auth/refresh", UriKind.Relative), new RefreshRequest(refreshToken));

    public static Task<HttpResponseMessage> SendAuthorizedAsync(this HttpClient client, HttpMethod method, string path, string accessToken)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client.SendAsync(request);
    }

    public static async Task<string?> ProblemCodeAsync(this HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
