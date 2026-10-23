using Ovrilo.Modules.Identity.Contracts;

namespace Ovrilo.Modules.Identity.Features;

/// <summary>Registers a new account and signs it in on the calling device.</summary>
public sealed record RegisterRequest(string Username, string DisplayName, string Email, string Password, string? DeviceLabel);

/// <summary>Signs in with a username or email and password.</summary>
public sealed record LoginRequest(string Login, string Password, string? DeviceLabel);

/// <summary>
/// Exchanges a refresh token for a new access token and a rotated refresh token. With <c>X-Ovrilo-Session: cookie</c>
/// the token comes from the refresh cookie and may be omitted here.
/// </summary>
public sealed record RefreshRequest(string? RefreshToken);

/// <summary>Tokens for a signed-in session. Store the refresh token in OS-protected storage only.</summary>
/// <param name="AccessToken">Short-lived bearer token for API calls.</param>
/// <param name="AccessTokenExpiresAt">Access token expiry (UTC).</param>
/// <param name="RefreshToken">
/// Single-use refresh token; replaced on every refresh. Null when it was delivered as an HttpOnly cookie
/// (<c>X-Ovrilo-Session: cookie</c>, browser client).
/// </param>
/// <param name="SessionExpiresAt">Absolute session expiry (UTC); refreshing does not extend it.</param>
/// <param name="User">The signed-in account.</param>
public sealed record AuthResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string? RefreshToken,
    DateTimeOffset SessionExpiresAt,
    CurrentUserResponse User);

/// <summary>The authenticated caller's own account.</summary>
/// <param name="AvatarAssetId">Media asset of the profile picture, or null for initials.</param>
/// <param name="Availability">The status the user chose (PUT /users/me/status).</param>
public sealed record CurrentUserResponse(
    Guid Id,
    string Username,
    string DisplayName,
    string Email,
    bool EmailVerified,
    DateTimeOffset CreatedAt,
    Guid? AvatarAssetId,
    Availability Availability = Availability.Online);

/// <summary>Sets the profile picture to a verified upload (purpose <c>avatar</c>) made by the caller.</summary>
public sealed record SetAvatarRequest(Guid AssetId);

/// <summary>
/// Realtime <c>UserUpdated</c> payload: a user's public profile changed. Sent to every community they belong to
/// and to their own other devices.
/// </summary>
public sealed record UserUpdatedEvent(Guid UserId, string DisplayName, Guid? AvatarAssetId);
