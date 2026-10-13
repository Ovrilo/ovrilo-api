using System.Security.Claims;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

using Ovrilo.Modules.Identity.Contracts;
using Ovrilo.Modules.Identity.Domain;
using Ovrilo.Modules.Identity.Persistence;

namespace Ovrilo.Modules.Identity.Features;

internal static class GetCurrentUser
{
    public static async Task<Results<Ok<CurrentUserResponse>, UnauthorizedHttpResult>> HandleAsync(
        ClaimsPrincipal principal,
        IdentityDbContext db,
        CancellationToken cancellationToken)
    {
        if (principal.GetUserId() is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        var user = await db.Users.AsNoTracking()
            .SingleOrDefaultAsync(u => u.Id == userId && u.Status == UserStatus.Active, cancellationToken);

        return user is null
            ? TypedResults.Unauthorized()
            : TypedResults.Ok(SessionIssuer.ToCurrentUser(user));
    }
}
