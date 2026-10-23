namespace Ovrilo.Modules.Identity.Contracts;

/// <summary>Public, in-process contract other modules use to query Identity.</summary>
public interface IIdentityModule
{
    /// <summary>Returns public profile data for an active account, or <c>null</c> if it does not exist or is not active.</summary>
    Task<PublicUser?> GetPublicUserAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Returns public profiles for the active accounts among <paramref name="userIds"/>; missing or inactive accounts are omitted.</summary>
    Task<IReadOnlyDictionary<Guid, PublicUser>> GetPublicUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default);

    /// <summary>Finds an active account by its public username (case-insensitive), or <c>null</c>.</summary>
    Task<PublicUser?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default);

    /// <summary>Returns whether the account exists and may use the service.</summary>
    Task<bool> IsAccountActiveAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>The status the user chose (online when the account is not active).</summary>
    Task<Availability> GetAvailabilityAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Stores the user's chosen status; false when the account is not active.</summary>
    Task<bool> SetAvailabilityAsync(Guid userId, Availability availability, CancellationToken cancellationToken = default);
}

/// <summary>
/// The status a user chooses. Others see Online, Away or DoNotDisturb while the user is connected;
/// Invisible looks offline to everyone else.
/// </summary>
public enum Availability
{
    Online,
    Away,
    DoNotDisturb,
    Invisible,
}

/// <summary>Non-sensitive account data safe to show to other users.</summary>
/// <param name="AvatarAssetId">Media asset of the profile picture, or null for initials.</param>
public sealed record PublicUser(Guid Id, string Username, string DisplayName, Guid? AvatarAssetId);
