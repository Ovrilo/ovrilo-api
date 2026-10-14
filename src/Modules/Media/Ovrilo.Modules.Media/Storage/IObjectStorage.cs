namespace Ovrilo.Modules.Media.Storage;

/// <summary>Size and type the storage provider reports for a stored object.</summary>
internal sealed record StoredObject(long SizeBytes, string? ContentType);

/// <summary>
/// Provider-neutral object storage boundary (ADR-0014). Implementations use only standard S3 operations
/// (ADR-0015) and never leak provider types to callers. Presigned URLs are bearer credentials: never log them.
/// </summary>
internal interface IObjectStorage
{
    /// <summary>Bucket that new objects are written to.</summary>
    string Bucket { get; }

    /// <summary>A URL that lets the holder PUT one object with exactly <paramref name="contentType"/> until it expires.</summary>
    Task<Uri> CreateUploadUrlAsync(string key, string contentType, TimeSpan lifetime, CancellationToken cancellationToken = default);

    /// <summary>A URL that lets the holder GET one object until it expires; the response carries <paramref name="contentType"/>.</summary>
    Task<Uri> CreateDownloadUrlAsync(string key, string contentType, TimeSpan lifetime, CancellationToken cancellationToken = default);

    /// <summary>Metadata of an object, or <c>null</c> when it does not exist.</summary>
    Task<StoredObject?> GetMetadataAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Up to <paramref name="length"/> bytes from the start of an object (fewer if it is shorter).</summary>
    Task<byte[]> ReadPrefixAsync(string key, int length, CancellationToken cancellationToken = default);

    /// <summary>Deletes an object. Succeeds when it does not exist.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}
