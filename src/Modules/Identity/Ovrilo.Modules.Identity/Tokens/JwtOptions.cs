using System.ComponentModel.DataAnnotations;
using System.Text;

namespace Ovrilo.Modules.Identity.Tokens;

internal sealed class JwtOptions : IValidatableObject
{
    public const string SectionName = "Identity:Jwt";
    public const int MinimumSigningKeyBytes = 32;

    [Required]
    public string Issuer { get; set; } = "ovrilo-api";

    [Required]
    public string Audience { get; set; } = "ovrilo-desktop";

    /// <summary>HMAC-SHA256 key; at least 256 bits. Supplied via secrets, never committed for non-development environments.</summary>
    public string SigningKey { get; set; } = string.Empty;

    [Range(1, 60)]
    public int AccessTokenLifetimeMinutes { get; set; } = 15;

    [Range(1, 365)]
    public int SessionLifetimeDays { get; set; } = 30;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Encoding.UTF8.GetByteCount(SigningKey) < MinimumSigningKeyBytes)
        {
            yield return new ValidationResult(
                $"{SectionName}:SigningKey must be at least {MinimumSigningKeyBytes} bytes.",
                [nameof(SigningKey)]);
        }
    }
}
