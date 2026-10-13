namespace Ovrilo.Modules.Communities.Contracts;

/// <summary>
/// Community permission flags (docs/domain/permissions.md). Bits are append-only: never reuse or renumber.
/// </summary>
[Flags]
public enum CommunityPermissions : long
{
    None = 0,
    ViewChannels = 1L << 0,
    SendMessages = 1L << 1,
    ConnectVoice = 1L << 2,
    CreateInvites = 1L << 3,
    ManageChannels = 1L << 4,
    ManageInvites = 1L << 5,
    ManageCommunity = 1L << 6,
    ManageMembers = 1L << 7,
}

/// <summary>System roles for Phase 01. Custom roles are future, additive work.</summary>
public enum CommunityRole
{
    Member = 0,
    Owner = 1,
}

/// <summary>In-process contract other modules use for community authorization decisions.</summary>
public interface ICommunitiesModule
{
    /// <summary>
    /// Effective permissions of <paramref name="userId"/> in the community; <see cref="CommunityPermissions.None"/>
    /// when the community does not exist or the user is not an active member.
    /// </summary>
    Task<CommunityPermissions> GetPermissionsAsync(Guid communityId, Guid userId, CancellationToken cancellationToken = default);
}
