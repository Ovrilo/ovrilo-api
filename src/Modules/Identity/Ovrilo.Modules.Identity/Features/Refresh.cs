using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

using Ovrilo.BuildingBlocks.Persistence;
using Ovrilo.Modules.Identity.Domain;
using Ovrilo.Modules.Identity.Persistence;
using Ovrilo.Modules.Identity.Tokens;

namespace Ovrilo.Modules.Identity.Features;

internal static class Refresh
{
    public static async Task<Results<Ok<AuthResponse>, ProblemHttpResult>> HandleAsync(
        RefreshRequest request,
        HttpContext httpContext,
        IdentityDbContext db,
        SessionIssuer sessions,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var token = RefreshCookie.IsRequested(httpContext.Request) ? RefreshCookie.Read(httpContext.Request) : request.RefreshToken;
        if (!RefreshTokens.TryParse(token, out var sessionId, out var secret))
        {
            return IdentityProblems.InvalidRefreshToken();
        }

        var now = timeProvider.GetDatabaseUtcNow();
        var session = await db.UserSessions.SingleOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null || !session.IsActive(now))
        {
            return IdentityProblems.InvalidRefreshToken();
        }

        if (!RefreshTokens.Matches(secret, session.RefreshTokenHash))
        {
            // A previously rotated (or forged) secret for a live session: assume theft and revoke the family.
            session.Revoke(now, RevocationReasons.RefreshTokenReuse);
            await TrySaveAsync(db, cancellationToken);
            return IdentityProblems.InvalidRefreshToken();
        }

        var user = await db.Users.SingleAsync(u => u.Id == session.UserId, cancellationToken);
        if (user.Status != UserStatus.Active)
        {
            return IdentityProblems.AccountUnavailable();
        }

        var refreshToken = sessions.Rotate(session);
        if (!await TrySaveAsync(db, cancellationToken))
        {
            // A concurrent refresh with the same token won; this caller's token is now stale.
            return IdentityProblems.InvalidRefreshToken();
        }

        return TypedResults.Ok(RefreshCookie.Deliver(httpContext, sessions.CreateResponse(user, session, refreshToken)));
    }

    private static async Task<bool> TrySaveAsync(IdentityDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }
}
