using System.Security.Claims;

using Microsoft.AspNetCore.Http.HttpResults;

using Ovrilo.Modules.Identity.Contracts;

namespace Ovrilo.Api.Realtime;

public sealed record SetStatusRequest(Availability Availability);

/// <summary>
/// The status a user shows to others. Stored by Identity, applied to live presence here, where the
/// realtime connections are.
/// </summary>
internal static class StatusEndpoints
{
    public static IEndpointRouteBuilder MapStatusEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPut("/users/me/status", SetAsync)
            .RequireAuthorization()
            .WithTags("Users")
            .WithName("SetStatus")
            .WithSummary("Sets the status others see: online, away, doNotDisturb or invisible (looks offline). Kept across sessions and devices.");
        return endpoints;
    }

    private static async Task<Results<NoContent, UnauthorizedHttpResult>> SetAsync(
        SetStatusRequest request,
        ClaimsPrincipal principal,
        IIdentityModule identity,
        PresenceTracker presence,
        CancellationToken cancellationToken)
    {
        if (principal.GetUserId() is not { } userId || !await identity.SetAvailabilityAsync(userId, request.Availability, cancellationToken))
        {
            return TypedResults.Unauthorized();
        }

        await presence.ChoseAsync(userId, request.Availability);
        return TypedResults.NoContent();
    }
}
