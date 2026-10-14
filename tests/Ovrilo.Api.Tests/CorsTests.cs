using System.Net;

using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

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
    public async Task EveryMethodTheApiUsesIsAllowedFromTheDesktop()
    {
        using var client = factory.CreateClient();
        var methods = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .SelectMany(e => e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
            .Where(m => m != "OPTIONS")
            .Distinct()
            .ToList();
        Assert.Contains("PUT", methods);

        foreach (var method in methods)
        {
            var response = await client.SendAsync(Preflight("http://tauri.localhost", method));
            var allowed = response.Headers.TryGetValues("Access-Control-Allow-Methods", out var values) ? string.Join(",", values) : "";
            Assert.True(allowed.Split(',').Select(v => v.Trim()).Contains(method), $"{method} is not allowed by CORS (allowed: {allowed}).");
        }
    }

    [Fact]
    public async Task OtherOriginsAreNotAllowed()
    {
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight("https://evil.example"));

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    private static HttpRequestMessage Preflight(string origin, string method = "POST")
    {
        var request = new HttpRequestMessage(HttpMethod.Options, new Uri("/auth/login", UriKind.Relative));
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", method);
        request.Headers.Add("Access-Control-Request-Headers", "content-type");
        return request;
    }
}
