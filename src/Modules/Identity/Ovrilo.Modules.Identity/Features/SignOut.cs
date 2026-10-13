using System.Security.Claims;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

using Ovrilo.BuildingBlocks.Persistence;
using Ovrilo.Modules.Identity.Contracts;
using Ovrilo.Modules.Identity.Domain;
using Ovrilo.Modules.Identity.Persistence;

namespace Ovrilo.Modules.Identity.Features;

internal static class SignOut
{
    /// <summary>Revokes the caller's current session. Idempotent.</summary>
    public static async Task<Results<NoContent, UnauthorizedHttpResult>> CurrentAsync(
        ClaimsPrincipal principal,
        IdentityDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (principal.GetUserId() is not { } userId || principal.GetSessionId() is not { } sessionId)
        {
            return TypedResults.Unauthorized();
        }

        var now = timeProvider.GetDatabaseUtcNow();
        await db.UserSessions
            .Where(s => s.Id == sessionId && s.UserId == userId && s.RevokedAt == null)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(s => s.RevokedAt, now)
                    .SetProperty(s => s.RevocationReason, RevocationReasons.SignOut),
                cancellationToken);

        return TypedResults.NoContent();
    }

    /// <summary>Revokes every active session of the caller, including the current one. Idempotent.</summary>
    public static async Task<Results<NoContent, UnauthorizedHttpResult>> EverywhereAsync(
        ClaimsPrincipal principal,
        IdentityDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (principal.GetUserId() is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        var now = timeProvider.GetDatabaseUtcNow();
        await db.UserSessions
            .Where(s => s.UserId == userId && s.RevokedAt == null)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(s => s.RevokedAt, now)
                    .SetProperty(s => s.RevocationReason, RevocationReasons.SignOutEverywhere),
                cancellationToken);

        return TypedResults.NoContent();
    }
}
