using System.Net;
using System.Net.Http.Headers;

using Microsoft.Extensions.Options;

using Ovrilo.Modules.Media.Storage;

namespace Ovrilo.Api.Tests.Media;

/// <summary>Runs <see cref="S3ObjectStorage"/> against RustFS and SeaweedFS (ADR-0015).</summary>
public sealed class StorageProviders : IAsyncLifetime
{
    public StorageContainer RustFs { get; } = StorageContainer.RustFs();

    public StorageContainer SeaweedFs { get; } = StorageContainer.SeaweedFs();

    public Task InitializeAsync() => Task.WhenAll(RustFs.StartAsync(), SeaweedFs.StartAsync());

    public async Task DisposeAsync()
    {
        await RustFs.DisposeAsync();
        await SeaweedFs.DisposeAsync();
    }

    public StorageContainer Get(string name) => name == RustFs.Name ? RustFs : SeaweedFs;
}

public sealed class StorageAdapterTests(StorageProviders providers) : IClassFixture<StorageProviders>
{
    public static TheoryData<string> Providers() => ["rustfs", "seaweedfs"];

    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private S3ObjectStorage CreateStorage(string provider)
    {
        var container = providers.Get(provider);
        return new S3ObjectStorage(Options.Create(new StorageOptions
        {
            ServiceUrl = container.ServiceUrl,
            Bucket = StorageContainer.Bucket,
            AccessKey = StorageContainer.AccessKey,
            SecretKey = StorageContainer.SecretKey,
        }));
    }

    private static async Task<HttpResponseMessage> PutAsync(Uri url, byte[] body, string contentType)
    {
        using var http = new HttpClient();
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return await http.PutAsync(url, content);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task PresignedUploadThenReadBack(string provider)
    {
        using var storage = CreateStorage(provider);
        var key = $"tests/{Guid.NewGuid():N}";

        var upload = await storage.CreateUploadUrlAsync(key, "image/gif", Lifetime);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(upload, TestImages.AnimatedGif, "image/gif")).StatusCode);

        var stored = await storage.GetMetadataAsync(key);
        Assert.NotNull(stored);
        Assert.Equal(TestImages.AnimatedGif.Length, stored.SizeBytes);
        Assert.Equal(TestImages.AnimatedGif[..10], await storage.ReadPrefixAsync(key, 10));
        Assert.Equal(TestImages.AnimatedGif, await storage.ReadPrefixAsync(key, 1024 * 1024));

        using var http = new HttpClient();
        var download = await http.GetAsync(await storage.CreateDownloadUrlAsync(key, "image/gif", Lifetime));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("image/gif", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal(TestImages.AnimatedGif, await download.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task UploadUrlIsBoundToItsContentType(string provider)
    {
        using var storage = CreateStorage(provider);
        var key = $"tests/{Guid.NewGuid():N}";

        var upload = await storage.CreateUploadUrlAsync(key, "image/png", Lifetime);

        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(upload, "<html></html>"u8.ToArray(), "text/html")).StatusCode);
        Assert.Null(await storage.GetMetadataAsync(key));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ObjectsAreNotPublic(string provider)
    {
        using var storage = CreateStorage(provider);
        var key = $"tests/{Guid.NewGuid():N}";
        await PutAsync(await storage.CreateUploadUrlAsync(key, "image/png", Lifetime), TestImages.Png, "image/png");

        using var http = new HttpClient();
        var anonymous = await http.GetAsync(new Uri(providers.Get(provider).ServiceUrl, $"{StorageContainer.Bucket}/{key}"));

        Assert.Equal(HttpStatusCode.Forbidden, anonymous.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DeleteIsIdempotent(string provider)
    {
        using var storage = CreateStorage(provider);
        var key = $"tests/{Guid.NewGuid():N}";
        await PutAsync(await storage.CreateUploadUrlAsync(key, "image/png", Lifetime), TestImages.Png, "image/png");

        await storage.DeleteAsync(key);
        await storage.DeleteAsync(key);

        Assert.Null(await storage.GetMetadataAsync(key));
    }
}
