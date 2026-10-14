namespace Ovrilo.Modules.Media.Features;

/// <summary>Upload policy (<c>Media:Limits</c>). Defaults suit avatars and icons; clients shrink still images before upload.</summary>
internal sealed class MediaLimits
{
    public const string SectionName = "Media:Limits";

    /// <summary>Largest accepted image file. Animated GIFs are stored as uploaded, so this bounds them.</summary>
    public long ImageMaxBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>Largest accepted width or height in pixels.</summary>
    public int ImageMaxDimension { get; set; } = 4096;

    /// <summary>Uploads a user may have started but not completed at once.</summary>
    public int MaxPendingUploadsPerUser { get; set; } = 5;

    public int UploadUrlLifetimeSeconds { get; set; } = 300;

    /// <summary>Extra time after the upload URL expires to call complete (an upload may finish just before expiry).</summary>
    public int CompleteGraceSeconds { get; set; } = 600;

    public int DownloadUrlLifetimeSeconds { get; set; } = 900;

    /// <summary>How long a verified upload may stay unused (not set as avatar/icon) before it is reclaimed.</summary>
    public int UnattachedRetentionHours { get; set; } = 24;
}
