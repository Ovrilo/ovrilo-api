using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

using Ovrilo.BuildingBlocks;
using Ovrilo.Modules.Identity.Contracts;

namespace Ovrilo.Modules.Social.Features;

/// <summary>Public profile of the other person in a friendship, request or block.</summary>
public sealed record SocialUserResponse(Guid Id, string Username, string DisplayName, Guid? AvatarAssetId)
{
    internal static SocialUserResponse From(PublicUser user) => new(user.Id, user.Username, user.DisplayName, user.AvatarAssetId);
}

public sealed record FriendResponse(SocialUserResponse User, DateTimeOffset Since);

/// <summary>A pending request. <c>User</c> is the other person: the sender for incoming, the recipient for outgoing.</summary>
public sealed record FriendRequestResponse(Guid Id, SocialUserResponse User, DateTimeOffset CreatedAt);

public sealed record FriendRequestListResponse(IReadOnlyList<FriendRequestResponse> Incoming, IReadOnlyList<FriendRequestResponse> Outgoing);

public sealed record SendFriendRequestRequest(string Username);

public enum FriendRequestOutcome
{
    /// <summary>A request is pending (new, or already sent before).</summary>
    Requested,

    /// <summary>They had already asked you, so you are now friends.</summary>
    Friends,
}

/// <summary>Result of sending a request: <c>Request</c> when <c>Requested</c>, <c>Friend</c> when <c>Friends</c>.</summary>
public sealed record SendFriendRequestResponse(FriendRequestOutcome Outcome, FriendRequestResponse? Request, FriendResponse? Friend);

public sealed record BlockedUserResponse(SocialUserResponse User, DateTimeOffset Since);

/// <summary>What changed for the receiver of a <see cref="SocialEvents.FriendsChanged"/> event.</summary>
public enum FriendsChange
{
    /// <summary>Someone sent you a friend request.</summary>
    RequestReceived,

    /// <summary>You and <c>User</c> are now friends.</summary>
    FriendAdded,

    /// <summary>Anything else (request sent, cancelled or declined, friend removed, block changed): refetch.</summary>
    Updated,
}

/// <summary>Sent to the user's own connections; clients refetch their friend and request lists.</summary>
public sealed record FriendsChangedEvent(FriendsChange Change, SocialUserResponse User);

/// <summary>Realtime event names (ovrilo-api/contracts/events/README.md).</summary>
public static class SocialEvents
{
    public const string FriendsChanged = "FriendsChanged";
}

internal static class SocialProblems
{
    /// <summary>Also returned when either user blocked the other, so blocks are not revealed.</summary>
    public static ProblemHttpResult UserNotFound() =>
        ApiProblems.Create(StatusCodes.Status404NotFound, "social.user_not_found", "No one with that username can be added.");

    public static ProblemHttpResult CannotAddSelf() =>
        ApiProblems.Create(StatusCodes.Status400BadRequest, "social.cannot_add_self", "You cannot add yourself.");

    public static ProblemHttpResult AlreadyFriends() =>
        ApiProblems.Create(StatusCodes.Status409Conflict, "social.already_friends", "You are already friends.");

    public static ProblemHttpResult RequestNotFound() =>
        ApiProblems.Create(StatusCodes.Status404NotFound, "social.request_not_found", "Friend request not found.");

    public static ProblemHttpResult RequestNotPending() =>
        ApiProblems.Create(StatusCodes.Status409Conflict, "social.request_not_pending", "This friend request was already answered or cancelled.");

    public static ProblemHttpResult CannotBlockSelf() =>
        ApiProblems.Create(StatusCodes.Status400BadRequest, "social.cannot_block_self", "You cannot block yourself.");
}
