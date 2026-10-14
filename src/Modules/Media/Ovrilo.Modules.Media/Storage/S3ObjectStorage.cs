using System.Net;

using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

using Microsoft.Extensions.Options;

namespace Ovrilo.Modules.Media.Storage;

/// <summary>
/// <see cref="IObjectStorage"/> over the S3 API: path-style addressing, SigV4, presigned PUT/GET, HEAD,
/// ranged GET and DELETE only, so RustFS, SeaweedFS or a managed S3 service work by configuration (ADR-0015).
/// </summary>
internal sealed class S3ObjectStorage : IObjectStorage, IDisposable
{
    private readonly AmazonS3Client _client;
    private readonly AmazonS3Client _signer;
    private readonly Protocol _publicProtocol;

    public S3ObjectStorage(IOptions<StorageOptions> options)
    {
        var settings = options.Value;
        var publicUrl = settings.PublicUrl ?? settings.ServiceUrl!;
        var credentials = new BasicAWSCredentials(settings.AccessKey, settings.SecretKey);

        Bucket = settings.Bucket;
        _client = new AmazonS3Client(credentials, CreateConfig(settings.ServiceUrl!, settings.Region));
        // Presigning is local computation; this client never sends requests. The signature covers the
        // host, so URLs must be signed for the address the desktop uses.
        _signer = new AmazonS3Client(credentials, CreateConfig(publicUrl, settings.Region));
        _publicProtocol = publicUrl.Scheme == Uri.UriSchemeHttps ? Protocol.HTTPS : Protocol.HTTP;
    }

    public string Bucket { get; }

    public async Task<Uri> CreateUploadUrlAsync(string key, string contentType, TimeSpan lifetime, CancellationToken cancellationToken = default) =>
        new(await _signer.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = Bucket,
            Key = key,
            Verb = HttpVerb.PUT,
            ContentType = contentType,
            Expires = DateTime.UtcNow.Add(lifetime),
            Protocol = _publicProtocol,
        }));

    public async Task<Uri> CreateDownloadUrlAsync(string key, string contentType, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = Bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(lifetime),
            Protocol = _publicProtocol,
        };
        // Serve the verified type, not whatever the uploader declared.
        request.ResponseHeaderOverrides.ContentType = contentType;
        return new(await _signer.GetPreSignedURLAsync(request));
    }

    public async Task<StoredObject?> GetMetadataAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var metadata = await _client.GetObjectMetadataAsync(Bucket, key, cancellationToken);
            return new StoredObject(metadata.ContentLength, metadata.Headers.ContentType);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<byte[]> ReadPrefixAsync(string key, int length, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        try
        {
            using var response = await _client.GetObjectAsync(
                new GetObjectRequest { BucketName = Bucket, Key = key, ByteRange = new ByteRange(0, length - 1) },
                cancellationToken);
            var buffer = new byte[length];
            var read = 0;
            int count;
            // Providers may ignore the range; never read more than requested.
            while (read < length && (count = await response.ResponseStream.ReadAsync(buffer.AsMemory(read), cancellationToken)) > 0)
            {
                read += count;
            }

            return buffer[..read];
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // Empty object.
            return [];
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.DeleteObjectAsync(Bucket, key, cancellationToken);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Already gone.
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _signer.Dispose();
    }

    private static AmazonS3Config CreateConfig(Uri serviceUrl, string region) => new()
    {
        ServiceURL = serviceUrl.ToString(),
        AuthenticationRegion = region,
        ForcePathStyle = true,
        // Newer SDK defaults add checksum headers some S3-compatible providers reject.
        RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
        ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
    };
}
