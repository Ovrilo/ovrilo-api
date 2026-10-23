using Microsoft.EntityFrameworkCore;

using Ovrilo.BuildingBlocks;
using Ovrilo.Modules.Communities.Contracts;
using Ovrilo.Modules.Communities.Domain;
using Ovrilo.Modules.Communities.Persistence;

namespace Ovrilo.Modules.Communities.Features;

/// <summary>Owners must hand over or delete their communities first; other memberships end.</summary>
internal sealed class CommunityAccountDeletion(CommunitiesDbContext db) : IAccountDeletionParticipant
{
    public async Task<string?> CheckAsync(Guid userId, CancellationToken cancellationToken = default) =>
        await db.Members.AnyAsync(
            m => m.UserId == userId && m.Role == CommunityRole.Owner && m.State == MembershipState.Active
                && db.Communities.Any(c => c.Id == m.CommunityId && c.DeletedAt == null),
            cancellationToken)
            ? "You own a community. Delete it first (there is no way to hand it over yet)."
            : null;

    public Task DeleteAsync(Guid userId, CancellationToken cancellationToken = default) =>
        // Active memberships end; bans stay as they are.
        db.Members
            .Where(m => m.UserId == userId && m.State == MembershipState.Active)
            .ExecuteUpdateAsync(set => set.SetProperty(m => m.State, MembershipState.Left), cancellationToken);
}
