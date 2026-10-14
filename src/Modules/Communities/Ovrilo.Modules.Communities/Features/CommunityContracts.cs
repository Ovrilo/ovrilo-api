using Ovrilo.Modules.Communities.Contracts;

namespace Ovrilo.Modules.Communities.Features;

public sealed record CreateCommunityRequest(string Name, string? Description);

/// <summary>A community as seen by one of its members.</summary>
/// <param name="MyRole">The caller's role in this community.</param>
/// <param name="IconAssetId">Media asset of the community icon, or null for initials.</param>
public sealed record CommunityResponse(
    Guid Id,
    string Name,
    string? Description,
    Guid OwnerId,
    CommunityRole MyRole,
    DateTimeOffset CreatedAt,
    Guid? IconAssetId);

/// <param name="AvatarAssetId">Media asset of the member's profile picture, or null for initials.</param>
public sealed record MemberResponse(
    Guid UserId,
    string Username,
    string DisplayName,
    string? Nickname,
    CommunityRole Role,
    DateTimeOffset JoinedAt,
    Guid? AvatarAssetId);

/// <summary>Sets the community icon to a verified upload (purpose <c>communityIcon</c>) made for this community.</summary>
public sealed record SetCommunityIconRequest(Guid AssetId);

/// <summary>Realtime <c>CommunityUpdated</c> payload: name, description or icon changed. Sent to the community.</summary>
public sealed record CommunityUpdatedEvent(Guid CommunityId, string Name, string? Description, Guid? IconAssetId);

/// <summary>Creates an expiring invitation.</summary>
/// <param name="MaxUses">1-100 uses; omitted means single-use.</param>
/// <param name="ExpiresInHours">1-720 hours; omitted means 24 hours.</param>
public sealed record CreateInvitationRequest(int? MaxUses, int? ExpiresInHours);

/// <summary>A newly created invitation. <paramref name="Code"/> is returned only once and cannot be retrieved later.</summary>
public sealed record InvitationResponse(
    Guid Id,
    Guid CommunityId,
    string Code,
    int MaxUses,
    DateTimeOffset ExpiresAt);

public sealed record AcceptInvitationRequest(string Code);
