namespace Ovrilo.Modules.Media.Contracts;

/// <summary>What an uploaded image is for. Decides who may upload and view it (docs/domain/media.md).</summary>
public enum MediaPurpose
{
    /// <summary>A user's profile picture. Any signed-in user may view it.</summary>
    Avatar = 0,

    /// <summary>A community's icon. Members of that community may view it; uploading needs ManageCommunity.</summary>
    CommunityIcon = 1,
}

/// <summary>A verified, ready-to-use media asset.</summary>
/// <param name="OwnerId">The user who uploaded it.</param>
/// <param name="CommunityId">The community an icon was uploaded for; null for avatars.</param>
public sealed record MediaAssetInfo(Guid Id, Guid OwnerId, MediaPurpose Purpose, Guid? CommunityId, string ContentType);

/// <summary>In-process contract other modules use to reference media they display (avatars, icons).</summary>
public interface IMediaModule
{
    /// <summary>
    /// Marks a verified asset as used by its owner module and returns it, or <c>null</c> when the asset does
    /// not exist, is not verified yet, was released, or was uploaded by another user or for another purpose.
    /// Used assets are never reclaimed as unattached uploads.
    /// </summary>
    Task<MediaAssetInfo?> AttachAsync(Guid assetId, Guid uploadedBy, MediaPurpose purpose, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases an asset that is no longer referenced. Its stored object is deleted shortly afterwards.
    /// Idempotent; unknown IDs are ignored.
    /// </summary>
    Task ReleaseAsync(Guid assetId, CancellationToken cancellationToken = default);
}
