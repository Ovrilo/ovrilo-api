using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

using Ovrilo.BuildingBlocks;

namespace Ovrilo.Modules.Identity.Features;

internal static class IdentityProblems
{
    public static ProblemHttpResult InvalidCredentials() =>
        ApiProblems.Create(StatusCodes.Status401Unauthorized, "identity.invalid_credentials", "Invalid username, email or password.");

    public static ProblemHttpResult InvalidRefreshToken() =>
        ApiProblems.Create(StatusCodes.Status401Unauthorized, "identity.invalid_refresh_token", "The refresh token is invalid, expired or revoked.");

    public static ProblemHttpResult AccountUnavailable() =>
        ApiProblems.Create(StatusCodes.Status403Forbidden, "identity.account_unavailable", "This account cannot sign in.");

    public static ProblemHttpResult UsernameTaken() =>
        ApiProblems.Create(StatusCodes.Status409Conflict, "identity.username_taken", "This username is already taken.");

    public static ProblemHttpResult EmailTaken() =>
        ApiProblems.Create(StatusCodes.Status409Conflict, "identity.email_taken", "An account with this email already exists.");
}
