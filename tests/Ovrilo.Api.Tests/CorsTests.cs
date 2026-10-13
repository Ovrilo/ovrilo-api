using System.Net;

namespace Ovrilo.Api.Tests;

[Collection(SharedApiFixture.Name)]
public sealed class CorsTests(OvriloApiFactory factory)
{
    [Theory]
    [InlineData("http://tauri.localhost")]
    [InlineData("tauri://localhost")]
    public async Task DesktopOriginsMayCallTheApi(string origin)
    {
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight(origin));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(origin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task OtherOriginsAreNotAllowed()
    {
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight("https://evil.example"));

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    private static HttpRequestMessage Preflight(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, new Uri("/auth/login", UriKind.Relative));
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type");
        return request;
    }
}
