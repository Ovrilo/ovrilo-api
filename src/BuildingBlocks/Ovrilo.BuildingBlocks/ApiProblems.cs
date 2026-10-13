using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ovrilo.BuildingBlocks;

/// <summary>
/// Problem Details (RFC 9457) with a stable machine-readable <c>code</c> extension and
/// <c>urn:ovrilo:problem:&lt;code&gt;</c> type (docs/development/CONTRACTS.md §1).
/// </summary>
public static class ApiProblems
{
    public const string ValidationFailedCode = "validation_failed";

    public static ProblemHttpResult Create(int statusCode, string code, string title) =>
        TypedResults.Problem(
            statusCode: statusCode,
            title: title,
            type: TypeFor(code),
            extensions: new Dictionary<string, object?> { ["code"] = code });

    public static ValidationProblem Validation(IDictionary<string, string[]> errors) =>
        TypedResults.ValidationProblem(
            errors,
            type: TypeFor(ValidationFailedCode),
            extensions: new Dictionary<string, object?> { ["code"] = ValidationFailedCode });

    public static string TypeFor(string code) => $"urn:ovrilo:problem:{code}";
}
