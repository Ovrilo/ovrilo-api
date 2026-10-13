namespace Ovrilo.Modules.Identity.Domain;

internal enum UserStatus
{
    PendingVerification = 0,
    Active = 1,
    Suspended = 2,
    Deleted = 3,
}

internal sealed class User
{
    public const int UsernameMinLength = 3;
    public const int UsernameMaxLength = 32;
    public const int DisplayNameMaxLength = 80;
    public const int EmailMaxLength = 320;

    public Guid Id { get; init; }

    /// <summary>Normalized (lowercase) public handle.</summary>
    public required string Username { get; init; }

    public required string DisplayName { get; set; }

    /// <summary>Normalized (trimmed, lowercase) login address. Not verified unless <see cref="EmailVerifiedAt"/> is set.</summary>
    public required string Email { get; set; }

    public DateTimeOffset? EmailVerifiedAt { get; set; }

    public required string PasswordHash { get; set; }

    public Guid? AvatarAssetId { get; set; }

    public UserStatus Status { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }
}
