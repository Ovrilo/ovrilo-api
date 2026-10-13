using Ovrilo.Modules.Communities.Contracts;

namespace Ovrilo.Modules.Communities.Features;

public sealed record CreateCommunityRequest(string Name, string? Description);

/// <summary>A community as seen by one of its members.</summary>
/// <param name="MyRole">The caller's role in this community.</param>
public sealed record CommunityResponse(
    Guid Id,
    string Name,
    string? Description,
    Guid OwnerId,
    CommunityRole MyRole,
    DateTimeOffset CreatedAt);

public sealed record MemberResponse(
    Guid UserId,
    string Username,
    string DisplayName,
    string? Nickname,
    CommunityRole Role,
    DateTimeOffset JoinedAt);

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
