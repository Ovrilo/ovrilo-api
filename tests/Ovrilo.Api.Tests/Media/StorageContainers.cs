using System.Net;

using Amazon.Runtime;
using Amazon.S3;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Ovrilo.Api.Tests.Media;

/// <summary>A disposable S3-compatible server with one private bucket and test-only credentials.</summary>
public sealed class StorageContainer : IAsyncDisposable
{
    public const string Bucket = "ovrilo-test";
    public const string AccessKey = "ovrilo-test-access";
    public const string SecretKey = "ovrilo-test-secret-key-not-a-secret";

    private readonly IContainer _container;
    private readonly bool _createsBucket;

    private StorageContainer(string name, IContainer container, bool createsBucket)
    {
        Name = name;
        _container = container;
        _createsBucket = createsBucket;
    }

    public string Name { get; }

    public Uri ServiceUrl => new($"http://{_container.Hostname}:{_container.GetMappedPublicPort(Port)}");

    private const int Port = 9000;

    /// <summary>The initial provider (ADR-0015), same image as ovrilo-infra.</summary>
    public static StorageContainer RustFs() => new(
        "rustfs",
        new ContainerBuilder("rustfs/rustfs:1.0.1")
            .WithEnvironment("RUSTFS_ACCESS_KEY", AccessKey)
            .WithEnvironment("RUSTFS_SECRET_KEY", SecretKey)
            .WithEnvironment("RUSTFS_ADDRESS", $":{Port}")
            .WithPortBinding(Port, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(Port).ForPath("/health")))
            .Build(),
        createsBucket: true);

    /// <summary>Second implementation that catches accidental provider coupling (ADR-0015).</summary>
    public static StorageContainer SeaweedFs() => new(
        "seaweedfs",
        new ContainerBuilder("chrislusf/seaweedfs:4.48")
            .WithEnvironment("AWS_ACCESS_KEY_ID", AccessKey)
            .WithEnvironment("AWS_SECRET_ACCESS_KEY", SecretKey)
            .WithCommand("mini", "-dir=/data", $"-s3.port={Port}", $"-bucket={Bucket}")
            .WithPortBinding(Port, true)
            // Anonymous requests are refused once the S3 gateway is up.
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(Port).ForPath($"/{Bucket}").ForStatusCode(HttpStatusCode.Forbidden)))
            .Build(),
        createsBucket: false);

    public async Task StartAsync()
    {
        await _container.StartAsync();
        if (_createsBucket)
        {
            using var client = CreateClient();
            await client.PutBucketAsync(Bucket);
        }
        else
        {
            // SeaweedFS creates the bucket during startup; wait until a signed request sees it.
            using var client = CreateClient();
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await client.GetBucketLocationAsync(Bucket);
                    break;
                }
                catch (AmazonS3Exception) when (attempt < 60)
                {
                    await Task.Delay(500);
                }
            }
        }
    }

    public AmazonS3Client CreateClient() => new(
        new BasicAWSCredentials(AccessKey, SecretKey),
        new AmazonS3Config
        {
            ServiceURL = ServiceUrl.ToString(),
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    public override string ToString() => Name;
}
