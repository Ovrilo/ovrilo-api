using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;

using Ovrilo.Api.Tests.Communities;
using Ovrilo.Api.Tests.Identity;
using Ovrilo.Modules.Media.Contracts;
using Ovrilo.Modules.Media.Features;

namespace Ovrilo.Api.Tests.Media;

/// <summary>The API can be deployed before object storage exists: media is off, everything else works.</summary>
[Collection(SharedApiFixture.Name)]
public sealed class MediaUnavailableTests(OvriloApiFactory factory)
{
    private WebApplicationFactory<Program> WithStorage(string serviceUrl, string accessKey = StorageContainer.AccessKey) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Media:Storage:ServiceUrl", serviceUrl);
            builder.UseSetting("Media:Storage:AccessKey", accessKey);
        });

    [Fact]
    public async Task WithoutStorageTheApiRunsAndMediaAnswers503()
    {
        await using var app = WithStorage(serviceUrl: "");
        using var user = await TestUser.RegisterAsync(app);

        var upload = await user.Client.PostAsJsonAsync(
            new Uri("/media/uploads", UriKind.Relative),
            new CreateUploadRequest(MediaPurpose.Avatar, "image/png", 100, null, null),
            TestJson.Options);
        var urls = await user.Client.PostAsJsonAsync(new Uri("/media/download-urls", UriKind.Relative), new DownloadUrlsRequest([Guid.NewGuid()]));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, upload.StatusCode);
        Assert.Equal("media.unavailable", await upload.ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, urls.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/users/me")).StatusCode);
    }

    [Fact]
    public void HalfConfiguredStorageFailsAtStartup()
    {
        using var app = WithStorage(serviceUrl: "http://storage.test:9000", accessKey: "");

        var error = Assert.ThrowsAny<Exception>(() => app.CreateClient());

        var chain = new List<Exception>();
        for (Exception? e = error; e is not null; e = e.InnerException)
        {
            chain.Add(e);
        }

        Assert.Contains(chain, e => e is OptionsValidationException);
    }
}
