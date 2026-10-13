using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ovrilo.Modules.Identity.Features;

/// <summary>Problem Details responses with stable machine-readable codes (docs/development/CONTRACTS.md §1).</summary>
internal static class IdentityProblems
{
    public const string InvalidCredentialsCode = "identity.invalid_credentials";
    public const string InvalidRefreshTokenCode = "identity.invalid_refresh_token";
    public const string AccountUnavailableCode = "identity.account_unavailable";
    public const string UsernameTakenCode = "identity.username_taken";
    public const string EmailTakenCode = "identity.email_taken";
    public const string ValidationFailedCode = "validation_failed";

    public static ProblemHttpResult InvalidCredentials() =>
        Create(StatusCodes.Status401Unauthorized, InvalidCredentialsCode, "Invalid username, email or password.");

    public static ProblemHttpResult InvalidRefreshToken() =>
        Create(StatusCodes.Status401Unauthorized, InvalidRefreshTokenCode, "The refresh token is invalid, expired or revoked.");

    public static ProblemHttpResult AccountUnavailable() =>
        Create(StatusCodes.Status403Forbidden, AccountUnavailableCode, "This account cannot sign in.");

    public static ProblemHttpResult UsernameTaken() =>
        Create(StatusCodes.Status409Conflict, UsernameTakenCode, "This username is already taken.");

    public static ProblemHttpResult EmailTaken() =>
        Create(StatusCodes.Status409Conflict, EmailTakenCode, "An account with this email already exists.");

    public static ValidationProblem Validation(IDictionary<string, string[]> errors) =>
        TypedResults.ValidationProblem(
            errors,
            type: Type(ValidationFailedCode),
            extensions: new Dictionary<string, object?> { ["code"] = ValidationFailedCode });

    private static ProblemHttpResult Create(int status, string code, string title) =>
        TypedResults.Problem(
            statusCode: status,
            title: title,
            type: Type(code),
            extensions: new Dictionary<string, object?> { ["code"] = code });

    private static string Type(string code) => $"urn:ovrilo:problem:{code}";
}
