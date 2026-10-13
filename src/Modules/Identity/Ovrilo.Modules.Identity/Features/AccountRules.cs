using System.Net.Mail;

using Ovrilo.Modules.Identity.Domain;

namespace Ovrilo.Modules.Identity.Features;

/// <summary>Input normalization and validation for account data (docs/domain/identity.md, ADR-0012).</summary>
internal static class AccountRules
{
    public const int PasswordMinLength = 8;
    public const int PasswordMaxLength = 128;

    private static readonly HashSet<string> ReservedUsernames = new(StringComparer.Ordinal)
    {
        "admin", "administrator", "api", "help", "mod", "moderator", "null", "official", "ovrilo",
        "owner", "root", "security", "staff", "support", "system", "undefined",
    };

    public static string NormalizeUsername(string? username) => (username ?? string.Empty).Trim().ToLowerInvariant();

    public static string NormalizeEmail(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();

    public static string NormalizeDisplayName(string? displayName) => (displayName ?? string.Empty).Trim();

    public static string? NormalizeDeviceLabel(string? deviceLabel)
    {
        var trimmed = deviceLabel?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= UserSession.DeviceLabelMaxLength ? trimmed : trimmed[..UserSession.DeviceLabelMaxLength];
    }

    public static void ValidateUsername(string normalized, Dictionary<string, string[]> errors)
    {
        if (normalized.Length is < User.UsernameMinLength or > User.UsernameMaxLength)
        {
            errors["username"] = [$"Username must be {User.UsernameMinLength}-{User.UsernameMaxLength} characters."];
        }
        else if (!normalized.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '.')
            || normalized[0] == '.' || normalized[^1] == '.' || normalized.Contains("..", StringComparison.Ordinal))
        {
            errors["username"] = ["Username may contain only letters, digits, '_' and '.', and cannot start, end or repeat '.'."];
        }
        else if (ReservedUsernames.Contains(normalized))
        {
            errors["username"] = ["This username is reserved."];
        }
    }

    public static void ValidateDisplayName(string normalized, Dictionary<string, string[]> errors)
    {
        if (normalized.Length is 0 or > User.DisplayNameMaxLength || normalized.Any(char.IsControl))
        {
            errors["displayName"] = [$"Display name must be 1-{User.DisplayNameMaxLength} characters without control characters."];
        }
    }

    public static void ValidateEmail(string normalized, Dictionary<string, string[]> errors)
    {
        if (normalized.Length is 0 or > User.EmailMaxLength
            || !MailAddress.TryCreate(normalized, out var address)
            || !string.Equals(address.Address, normalized, StringComparison.Ordinal)
            || !address.Host.Contains('.', StringComparison.Ordinal))
        {
            errors["email"] = ["Enter a valid email address."];
        }
    }

    public static void ValidatePassword(string? password, Dictionary<string, string[]> errors)
    {
        if (password is null || password.Length is < PasswordMinLength or > PasswordMaxLength)
        {
            errors["password"] = [$"Password must be {PasswordMinLength}-{PasswordMaxLength} characters."];
        }
    }
}
