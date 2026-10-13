using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using Npgsql;

using Ovrilo.Modules.Identity.Domain;
using Ovrilo.Modules.Identity.Persistence;

namespace Ovrilo.Modules.Identity.Features;

internal static class Register
{
    public static async Task<Results<Created<AuthResponse>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        RegisterRequest request,
        IdentityDbContext db,
        IPasswordHasher<User> passwordHasher,
        SessionIssuer sessions,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var username = AccountRules.NormalizeUsername(request.Username);
        var displayName = AccountRules.NormalizeDisplayName(request.DisplayName);
        var email = AccountRules.NormalizeEmail(request.Email);

        var errors = new Dictionary<string, string[]>();
        AccountRules.ValidateUsername(username, errors);
        AccountRules.ValidateDisplayName(displayName, errors);
        AccountRules.ValidateEmail(email, errors);
        AccountRules.ValidatePassword(request.Password, errors);
        if (errors.Count > 0)
        {
            return IdentityProblems.Validation(errors);
        }

        if (await db.Users.AnyAsync(u => u.Username == username, cancellationToken))
        {
            return IdentityProblems.UsernameTaken();
        }

        if (await db.Users.AnyAsync(u => u.Email == email, cancellationToken))
        {
            return IdentityProblems.EmailTaken();
        }

        var now = timeProvider.GetDatabaseUtcNow();
        var user = new User
        {
            Id = Guid.CreateVersion7(now),
            Username = username,
            DisplayName = displayName,
            Email = email,
            PasswordHash = string.Empty,
            // Email verification is not required yet (ADR-0012); accounts are usable immediately.
            Status = UserStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
        db.Users.Add(user);

        var (session, refreshToken) = sessions.StartSession(user, request.DeviceLabel);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            // A concurrent registration claimed the username or email between the checks and the insert.
            return pg.ConstraintName?.Contains("username", StringComparison.Ordinal) == true
                ? IdentityProblems.UsernameTaken()
                : IdentityProblems.EmailTaken();
        }

        return TypedResults.Created("/users/me", sessions.CreateResponse(user, session, refreshToken));
    }
}
