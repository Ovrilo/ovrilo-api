using System.Security.Claims;

namespace Ovrilo.Modules.Identity.Contracts;

/// <summary>Claim names in Ovrilo access tokens and helpers to read the authenticated caller.</summary>
public static class IdentityClaims
{
    public const string UserId = "sub";
    public const string SessionId = "sid";

    /// <summary>Returns the authenticated user's ID, or <c>null</c> if the principal is anonymous or malformed.</summary>
    public static Guid? GetUserId(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return Guid.TryParse(principal.FindFirst(UserId)?.Value, out var id) ? id : null;
    }

    /// <summary>Returns the session ID of the current access token, or <c>null</c>.</summary>
    public static Guid? GetSessionId(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return Guid.TryParse(principal.FindFirst(SessionId)?.Value, out var id) ? id : null;
    }
}
