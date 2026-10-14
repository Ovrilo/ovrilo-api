using Ovrilo.Modules.Media.Contracts;

namespace Ovrilo.Modules.Media.Features;

/// <summary>Asks for a URL to upload one image to.</summary>
/// <param name="ContentType">image/png, image/jpeg, image/gif or image/webp. The upload must send exactly this Content-Type.</param>
/// <param name="SizeBytes">Size of the file to upload.</param>
/// <param name="Filename">Original file name; display-only.</param>
/// <param name="CommunityId">Required for <see cref="MediaPurpose.CommunityIcon"/>; must be omitted otherwise.</param>
public sealed record CreateUploadRequest(MediaPurpose Purpose, string ContentType, long SizeBytes, string? Filename, Guid? CommunityId);

/// <summary>
/// Where to upload: send the file bytes with <c>PUT</c> to <c>UploadUrl</c>, header <c>Content-Type: ContentType</c>,
/// before <c>ExpiresAt</c>; then call complete. Treat the URL as a secret.
/// </summary>
public sealed record UploadResponse(Guid AssetId, Uri UploadUrl, string ContentType, long MaxBytes, DateTimeOffset ExpiresAt);

/// <summary>A verified image.</summary>
public sealed record MediaAssetResponse(Guid Id, MediaPurpose Purpose, string ContentType, long SizeBytes, int Width, int Height);

/// <summary>Up to 100 asset IDs to get temporary download URLs for.</summary>
public sealed record DownloadUrlsRequest(IReadOnlyList<Guid> AssetIds);

/// <summary>A temporary URL to read one asset. Asset IDs never change content, so the bytes can be cached by ID.</summary>
public sealed record DownloadUrl(Guid AssetId, Uri Url, string ContentType, DateTimeOffset ExpiresAt);

/// <summary>URLs for the requested assets the caller may view; unknown, unready or hidden assets are omitted.</summary>
public sealed record DownloadUrlsResponse(IReadOnlyList<DownloadUrl> Urls);
