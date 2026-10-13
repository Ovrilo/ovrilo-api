namespace Ovrilo.Modules.Identity.Features;

/// <summary>Registers a new account and signs it in on the calling device.</summary>
public sealed record RegisterRequest(string Username, string DisplayName, string Email, string Password, string? DeviceLabel);

/// <summary>Signs in with a username or email and password.</summary>
public sealed record LoginRequest(string Login, string Password, string? DeviceLabel);

/// <summary>Exchanges a refresh token for a new access token and a rotated refresh token.</summary>
public sealed record RefreshRequest(string RefreshToken);

/// <summary>Tokens for a signed-in session. Store the refresh token in OS-protected storage only.</summary>
/// <param name="AccessToken">Short-lived bearer token for API calls.</param>
/// <param name="AccessTokenExpiresAt">Access token expiry (UTC).</param>
/// <param name="RefreshToken">Single-use refresh token; replaced on every refresh.</param>
/// <param name="SessionExpiresAt">Absolute session expiry (UTC); refreshing does not extend it.</param>
/// <param name="User">The signed-in account.</param>
public sealed record AuthResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset SessionExpiresAt,
    CurrentUserResponse User);

/// <summary>The authenticated caller's own account.</summary>
public sealed record CurrentUserResponse(
    Guid Id,
    string Username,
    string DisplayName,
    string Email,
    bool EmailVerified,
    DateTimeOffset CreatedAt);
