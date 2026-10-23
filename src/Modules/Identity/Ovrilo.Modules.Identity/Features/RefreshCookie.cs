using Microsoft.AspNetCore.Http;

namespace Ovrilo.Modules.Identity.Features;

/// <summary>
/// Cookie transport for refresh tokens, used by the browser client that has no OS credential store (ADR-0012 amendment,
/// ADR-0019). Opt-in per request with <c>X-Ovrilo-Session: cookie</c>; without the header tokens travel in the body.
/// The header is not a CORS-safelisted one, so cross-origin callers must pass a preflight against the allowed origins.
/// </summary>
internal static class RefreshCookie
{
    public const string HeaderName = "X-Ovrilo-Session";
    public const string HeaderValue = "cookie";

    // __Secure- makes browsers refuse the cookie unless it is set with Secure over a secure context.
    public const string Name = "__Secure-ovrilo-refresh";

    /// <summary>The cookie is only sent to the auth endpoints.</summary>
    private const string Path = "/auth";

    public static bool IsRequested(HttpRequest request) =>
        string.Equals(request.Headers[HeaderName], HeaderValue, StringComparison.OrdinalIgnoreCase);

    public static string? Read(HttpRequest request) => request.Cookies[Name];

    /// <summary>Stores the token in the cookie and returns the response with the token removed from the body.</summary>
    public static AuthResponse Deliver(HttpContext context, AuthResponse response)
    {
        if (!IsRequested(context.Request) || response.RefreshToken is null)
        {
            return response;
        }

        context.Response.Cookies.Append(Name, response.RefreshToken, Options(response.SessionExpiresAt));
        return response with { RefreshToken = null };
    }

    public static void Clear(HttpContext context)
    {
        if (IsRequested(context.Request))
        {
            context.Response.Cookies.Delete(Name, Options(expires: null));
        }
    }

    private static CookieOptions Options(DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = Path,
        Expires = expires,
        IsEssential = true,
    };
}
