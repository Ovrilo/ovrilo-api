using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

using Ovrilo.BuildingBlocks;

namespace Ovrilo.Modules.Communities.Features;

internal static class CommunityProblems
{
    /// <summary>Also returned when the caller is not a member, so existence is not revealed.</summary>
    public static ProblemHttpResult NotFound() =>
        ApiProblems.Create(StatusCodes.Status404NotFound, "communities.not_found", "Community not found.");

    public static ProblemHttpResult PermissionDenied() =>
        ApiProblems.Create(StatusCodes.Status403Forbidden, "communities.permission_denied", "You do not have permission to do this in this community.");

    public static ProblemHttpResult OwnerCannotLeave() =>
        ApiProblems.Create(StatusCodes.Status409Conflict, "communities.owner_cannot_leave", "The owner cannot leave the community.");

    /// <summary>Unknown, expired, revoked and exhausted invitations are indistinguishable to the caller.</summary>
    public static ProblemHttpResult InvitationInvalid() =>
        ApiProblems.Create(StatusCodes.Status404NotFound, "communities.invitation_invalid", "This invitation is invalid or has expired.");

    public static ProblemHttpResult InvitationNotFound() =>
        ApiProblems.Create(StatusCodes.Status404NotFound, "communities.invitation_not_found", "Invitation not found.");

    public static ProblemHttpResult Banned() =>
        ApiProblems.Create(StatusCodes.Status403Forbidden, "communities.banned", "You cannot join this community.");
}
