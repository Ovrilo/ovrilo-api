using System.Net.Http.Json;

using Ovrilo.Api.Tests.Identity;
using Ovrilo.Modules.Channels.Features;
using Ovrilo.Modules.Communities.Features;
using Ovrilo.Modules.Identity.Features;

namespace Ovrilo.Api.Tests.Communities;

/// <summary>A signed-in test user with an authenticated client.</summary>
internal sealed class TestUser(HttpClient client, AuthResponse auth) : IDisposable
{
    public HttpClient Client { get; } = client;

    public AuthResponse Auth { get; } = auth;

    public Guid Id => Auth.User.Id;

    public static async Task<TestUser> RegisterAsync(OvriloApiFactory factory)
    {
        var client = factory.CreateClient();
        var auth = await client.RegisterNewAsync();
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.AccessToken);
        return new TestUser(client, auth);
    }

    public Task<HttpResponseMessage> PostAsync<T>(string path, T body) =>
        Client.PostAsJsonAsync(new Uri(path, UriKind.Relative), body);

    public Task<HttpResponseMessage> GetAsync(string path) => Client.GetAsync(new Uri(path, UriKind.Relative));

    public async Task<CommunityResponse> CreateCommunityAsync(string name = "Friends")
    {
        var response = await PostAsync("/communities", new CreateCommunityRequest(name, "Test community"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CommunityResponse>(TestJson.Options))!;
    }

    public async Task<InvitationResponse> CreateInvitationAsync(Guid communityId, int? maxUses = null)
    {
        var response = await PostAsync($"/communities/{communityId}/invitations", new CreateInvitationRequest(maxUses, null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<InvitationResponse>(TestJson.Options))!;
    }

    public Task<HttpResponseMessage> AcceptAsync(string code) => PostAsync("/invitations/accept", new AcceptInvitationRequest(code));

    public async Task<ChannelResponse> CreateChannelAsync(Guid communityId, string name, string type)
    {
        var response = await PostAsync($"/communities/{communityId}/channels", new { name, type });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ChannelResponse>(TestJson.Options))!;
    }

    public void Dispose() => Client.Dispose();
}
