namespace Ovrilo.Modules.Identity.Domain;

/// <summary>
/// A per-device sign-in. The session ID is also the refresh token family: every refresh rotates the
/// secret, and presenting a stale secret revokes the session (ADR-0012).
/// </summary>
internal sealed class UserSession
{
    public const int DeviceLabelMaxLength = 120;

    public Guid Id { get; init; }

    public Guid UserId { get; init; }

    /// <summary>Untrusted, display-only label supplied by the client.</summary>
    public string? DeviceLabel { get; init; }

    /// <summary>Hex-encoded SHA-256 of the current refresh token secret. The raw secret is never stored.</summary>
    public required string RefreshTokenHash { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>Absolute expiry; refreshing does not extend it.</summary>
    public DateTimeOffset ExpiresAt { get; init; }

    public DateTimeOffset? RevokedAt { get; set; }

    public string? RevocationReason { get; set; }

    /// <summary>PostgreSQL <c>xmin</c> concurrency token; prevents two concurrent refreshes from both succeeding.</summary>
    public uint Version { get; set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;

    public void Revoke(DateTimeOffset now, string reason)
    {
        if (RevokedAt is not null)
        {
            return;
        }

        RevokedAt = now;
        RevocationReason = reason;
    }
}

internal static class RevocationReasons
{
    public const string SignOut = "sign_out";
    public const string SignOutEverywhere = "sign_out_everywhere";
    public const string RefreshTokenReuse = "refresh_token_reuse";
    public const string AccountDeleted = "account_deleted";
}
