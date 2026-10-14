namespace Ovrilo.Modules.Media.Storage;

/// <summary>
/// S3-compatible storage settings (<c>Media:Storage</c>). Credentials are secrets: Coolify only, never in the repository.
/// Media is off until <see cref="ServiceUrl"/> is set: the API still starts and media endpoints answer 503, so the API
/// can be deployed before the storage resource exists.
/// </summary>
internal sealed class StorageOptions
{
    public const string SectionName = "Media:Storage";

    /// <summary>Endpoint the API itself uses (internal network address in production). Unset: media is disabled.</summary>
    public Uri? ServiceUrl { get; set; }

    /// <summary>Endpoint the desktop reaches; presigned URLs are signed for this host. Defaults to <see cref="ServiceUrl"/>.</summary>
    public Uri? PublicUrl { get; set; }

    public string Bucket { get; set; } = "";

    public string AccessKey { get; set; } = "";

    public string SecretKey { get; set; } = "";

    public string Region { get; set; } = "us-east-1";

    public bool IsConfigured => ServiceUrl is { IsAbsoluteUri: true };

    /// <summary>Half-configured storage is a deployment mistake: fail at startup instead of at the first upload.</summary>
    public bool IsValid =>
        !IsConfigured || (Bucket.Length > 0 && AccessKey.Length > 0 && SecretKey.Length > 0);
}
