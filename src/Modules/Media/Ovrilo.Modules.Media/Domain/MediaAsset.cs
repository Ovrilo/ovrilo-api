using Ovrilo.Modules.Media.Contracts;

namespace Ovrilo.Modules.Media.Domain;

internal enum MediaAssetStatus
{
    /// <summary>Upload URL issued; the object may or may not exist yet.</summary>
    Pending = 0,

    /// <summary>Object verified (size, format, dimensions); downloadable.</summary>
    Ready = 1,

    /// <summary>Verification failed; the object is deleted by the janitor.</summary>
    Rejected = 2,

    /// <summary>Released, expired or abandoned; the object is deleted by the janitor.</summary>
    Deleted = 3,
}

/// <summary>Metadata of one stored object. The bytes live in object storage under <see cref="ObjectKey"/> (ADR-0014).</summary>
internal sealed class MediaAsset
{
    public const int OriginalFilenameMaxLength = 255;

    public Guid Id { get; init; }

    /// <summary>The user who initiated the upload.</summary>
    public Guid OwnerId { get; init; }

    public MediaPurpose Purpose { get; init; }

    /// <summary>The community a <see cref="MediaPurpose.CommunityIcon"/> belongs to.</summary>
    public Guid? CommunityId { get; init; }

    public required string Bucket { get; init; }

    /// <summary>Random, non-user-derived key.</summary>
    public required string ObjectKey { get; init; }

    /// <summary>Untrusted, display-only.</summary>
    public string? OriginalFilename { get; init; }

    /// <summary>Untrusted MIME type the client declared; the upload URL only accepts this type.</summary>
    public required string DeclaredContentType { get; init; }

    /// <summary>Byte limit for this upload; checked again after the upload because presigned PUTs cannot enforce it.</summary>
    public long MaxBytes { get; init; }

    public DateTimeOffset UploadExpiresAt { get; init; }

    public string? DetectedContentType { get; set; }

    public long? SizeBytes { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public MediaAssetStatus Status { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? UploadedAt { get; set; }

    /// <summary>When a module started using the asset (avatar, icon). Ready assets never attached are reclaimed.</summary>
    public DateTimeOffset? AttachedAt { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>When the stored object was deleted; null while deletion is outstanding.</summary>
    public DateTimeOffset? ObjectPurgedAt { get; set; }
}
