namespace Ovrilo.BuildingBlocks;

/// <summary>
/// A module that keeps data about users takes part in deleting an account (registered by each
/// module, called by Identity). Deletion is not one transaction across modules: each step must be
/// idempotent, so a retry finishes what a failed attempt left.
/// </summary>
public interface IAccountDeletionParticipant
{
    /// <summary>Why the account cannot be deleted yet (shown to the user), or null.</summary>
    Task<string?> CheckAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

    /// <summary>Removes or detaches the user's data in this module; the account is already gone.</summary>
    Task DeleteAsync(Guid userId, CancellationToken cancellationToken = default);
}
