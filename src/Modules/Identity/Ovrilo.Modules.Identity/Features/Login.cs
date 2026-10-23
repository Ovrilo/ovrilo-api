using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using Ovrilo.BuildingBlocks.Persistence;
using Ovrilo.Modules.Identity.Domain;
using Ovrilo.Modules.Identity.Persistence;

namespace Ovrilo.Modules.Identity.Features;

internal static class Login
{
    public static async Task<Results<Ok<AuthResponse>, ProblemHttpResult>> HandleAsync(
        LoginRequest request,
        HttpContext httpContext,
        IdentityDbContext db,
        IPasswordHasher<User> passwordHasher,
        DummyPasswordHash dummyHash,
        SessionIssuer sessions,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var login = (request.Login ?? string.Empty).Trim().ToLowerInvariant();
        var password = request.Password ?? string.Empty;

        var user = login.Length == 0
            ? null
            : login.Contains('@', StringComparison.Ordinal)
                ? await db.Users.SingleOrDefaultAsync(u => u.Email == login, cancellationToken)
                : await db.Users.SingleOrDefaultAsync(u => u.Username == login, cancellationToken);

        if (user is null || user.Status == UserStatus.Deleted)
        {
            // Verify against a dummy hash so unknown accounts take comparable time.
            passwordHasher.VerifyHashedPassword(null!, dummyHash.Value, password);
            return IdentityProblems.InvalidCredentials();
        }

        var verification = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (verification == PasswordVerificationResult.Failed)
        {
            return IdentityProblems.InvalidCredentials();
        }

        if (user.Status != UserStatus.Active)
        {
            return IdentityProblems.AccountUnavailable();
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = passwordHasher.HashPassword(user, password);
            user.UpdatedAt = timeProvider.GetDatabaseUtcNow();
        }

        var (session, refreshToken) = sessions.StartSession(user, request.DeviceLabel);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(RefreshCookie.Deliver(httpContext, sessions.CreateResponse(user, session, refreshToken)));
    }
}

/// <summary>A hash of a random password, computed once, used to equalize timing for unknown accounts.</summary>
internal sealed class DummyPasswordHash(IPasswordHasher<User> passwordHasher)
{
    private readonly Lazy<string> _value = new(() => passwordHasher.HashPassword(null!, Guid.NewGuid().ToString("N")));

    public string Value => _value.Value;
}
