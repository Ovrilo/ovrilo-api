using System.ComponentModel.DataAnnotations;
using System.Text;

namespace Ovrilo.Modules.VoiceSessions.LiveKit;

internal sealed class LiveKitOptions : IValidatableObject
{
    public const string SectionName = "VoiceSessions:LiveKit";
    public const int MinimumApiSecretBytes = 32;

    /// <summary>WebSocket URL clients connect to (returned to the desktop with each grant).</summary>
    [Required]
    public string ServerUrl { get; set; } = string.Empty;

    [Required]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>LiveKit API secret; supplied via secrets, never committed for non-development environments.</summary>
    public string ApiSecret { get; set; } = string.Empty;

    /// <summary>Validity window for connecting. LiveKit refreshes the token for connected participants.</summary>
    [Range(1, 60)]
    public int TokenLifetimeMinutes { get; set; } = 10;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var url) || url.Scheme is not ("ws" or "wss"))
        {
            yield return new ValidationResult($"{SectionName}:ServerUrl must be a ws:// or wss:// URL.", [nameof(ServerUrl)]);
        }

        if (Encoding.UTF8.GetByteCount(ApiSecret) < MinimumApiSecretBytes)
        {
            yield return new ValidationResult(
                $"{SectionName}:ApiSecret must be at least {MinimumApiSecretBytes} bytes.",
                [nameof(ApiSecret)]);
        }
    }
}
