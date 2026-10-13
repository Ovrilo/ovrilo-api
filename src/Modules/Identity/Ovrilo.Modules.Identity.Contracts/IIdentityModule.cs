namespace Ovrilo.Modules.Identity.Contracts;

/// <summary>Public, in-process contract other modules use to query Identity.</summary>
public interface IIdentityModule
{
    /// <summary>Returns public profile data for an active account, or <c>null</c> if it does not exist or is not active.</summary>
    Task<PublicUser?> GetPublicUserAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Returns public profiles for the active accounts among <paramref name="userIds"/>; missing or inactive accounts are omitted.</summary>
    Task<IReadOnlyDictionary<Guid, PublicUser>> GetPublicUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default);

    /// <summary>Returns whether the account exists and may use the service.</summary>
    Task<bool> IsAccountActiveAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>Non-sensitive account data safe to show to other users.</summary>
public sealed record PublicUser(Guid Id, string Username, string DisplayName);
