using Ovrilo.Modules.Communities.Contracts;

namespace Ovrilo.Modules.Communities.Domain;

internal enum CommunityVisibility
{
    Private = 0,
}

internal sealed class Community
{
    public const int NameMaxLength = 100;
    public const int DescriptionMaxLength = 500;

    public Guid Id { get; init; }

    public Guid OwnerId { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public Guid? IconAssetId { get; set; }

    public CommunityVisibility Visibility { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }
}

internal enum MembershipState
{
    Active = 0,
    Left = 1,
    Banned = 2,
}

internal sealed class CommunityMember
{
    public const int NicknameMaxLength = 80;

    public Guid Id { get; init; }

    public Guid CommunityId { get; init; }

    public Guid UserId { get; init; }

    public CommunityRole Role { get; set; }

    public MembershipState State { get; set; }

    public string? Nickname { get; set; }

    public DateTimeOffset JoinedAt { get; set; }

    public CommunityPermissions Permissions => State == MembershipState.Active ? RolePermissions.For(Role) : CommunityPermissions.None;
}

internal static class RolePermissions
{
    private const CommunityPermissions MemberPermissions =
        CommunityPermissions.ViewChannels
        | CommunityPermissions.SendMessages
        | CommunityPermissions.ConnectVoice
        | CommunityPermissions.CreateInvites
        | CommunityPermissions.ShareScreen;

    private const CommunityPermissions OwnerPermissions =
        MemberPermissions
        | CommunityPermissions.ManageChannels
        | CommunityPermissions.ManageInvites
        | CommunityPermissions.ManageCommunity
        | CommunityPermissions.ManageMembers
        | CommunityPermissions.ManageMessages;

    public static CommunityPermissions For(CommunityRole role) => role switch
    {
        CommunityRole.Owner => OwnerPermissions,
        CommunityRole.Member => MemberPermissions,
        _ => CommunityPermissions.None,
    };
}

internal sealed class Invitation
{
    public const int MaxUsesLimit = 100;
    public const int MaxLifetimeHours = 24 * 30;

    public Guid Id { get; init; }

    public Guid CommunityId { get; init; }

    public Guid CreatedBy { get; init; }

    /// <summary>Hex SHA-256 of the invitation code; the code itself is shown once and never stored.</summary>
    public required string CodeHash { get; init; }

    public int? MaxUses { get; init; }

    public int UseCount { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    public DateTimeOffset? RevokedAt { get; set; }
}
