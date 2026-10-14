using System.ComponentModel.DataAnnotations;

namespace Ovrilo.Modules.Media.Storage;

/// <summary>S3-compatible storage settings (<c>Media:Storage</c>). Credentials are secrets: Coolify only, never in the repository.</summary>
internal sealed class StorageOptions
{
    public const string SectionName = "Media:Storage";

    /// <summary>Endpoint the API itself uses (internal network address in production).</summary>
    [Required]
    public Uri? ServiceUrl { get; set; }

    /// <summary>Endpoint the desktop reaches; presigned URLs are signed for this host. Defaults to <see cref="ServiceUrl"/>.</summary>
    public Uri? PublicUrl { get; set; }

    [Required]
    public string Bucket { get; set; } = "";

    [Required]
    public string AccessKey { get; set; } = "";

    [Required]
    public string SecretKey { get; set; } = "";

    public string Region { get; set; } = "us-east-1";
}
