using Microsoft.Extensions.Options;

using Ovrilo.Modules.Identity.Domain;
using Ovrilo.Modules.Identity.Persistence;
using Ovrilo.Modules.Identity.Tokens;

namespace Ovrilo.Modules.Identity.Features;

/// <summary>Creates and rotates per-device sessions and builds the token response.</summary>
internal sealed class SessionIssuer(
    IdentityDbContext db,
    AccessTokenIssuer accessTokens,
    IOptions<JwtOptions> jwtOptions,
    TimeProvider timeProvider)
{
    /// <summary>Adds a new session to the context; the caller saves changes.</summary>
    public (UserSession Session, string RefreshToken) StartSession(User user, string? deviceLabel)
    {
        var now = timeProvider.GetDatabaseUtcNow();
        var sessionId = Guid.CreateVersion7(now);
        var (refreshToken, hash) = RefreshTokens.Create(sessionId);

        var session = new UserSession
        {
            Id = sessionId,
            UserId = user.Id,
            DeviceLabel = AccountRules.NormalizeDeviceLabel(deviceLabel),
            RefreshTokenHash = hash,
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = now.AddDays(jwtOptions.Value.SessionLifetimeDays),
        };

        db.UserSessions.Add(session);
        return (session, refreshToken);
    }

    /// <summary>Replaces the session's refresh secret; the caller saves changes.</summary>
    public string Rotate(UserSession session)
    {
        var (refreshToken, hash) = RefreshTokens.Create(session.Id);
        session.RefreshTokenHash = hash;
        session.LastSeenAt = timeProvider.GetDatabaseUtcNow();
        return refreshToken;
    }

    public AuthResponse CreateResponse(User user, UserSession session, string refreshToken)
    {
        var access = accessTokens.Issue(user.Id, session.Id);
        return new AuthResponse(access.Token, access.ExpiresAt, refreshToken, session.ExpiresAt, ToCurrentUser(user));
    }

    public static CurrentUserResponse ToCurrentUser(User user) =>
        new(user.Id, user.Username, user.DisplayName, user.Email, user.EmailVerifiedAt is not null, user.CreatedAt);
}
