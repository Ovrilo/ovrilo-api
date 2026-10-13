using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Ovrilo.Api.System;

namespace Ovrilo.Api.Tests;

public sealed class SystemEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task HealthReturnsOk()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task VersionReturnsBuildVersion()
    {
        using var client = factory.CreateClient();

        var body = await client.GetFromJsonAsync<VersionResponse>(new Uri("/version", UriKind.Relative));

        Assert.False(string.IsNullOrWhiteSpace(body?.Version));
        Assert.Equal(ApiContract.Version, body?.ContractVersion);
    }

    [Fact]
    public async Task UnknownRouteReturnsProblemDetails()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/does-not-exist", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}
