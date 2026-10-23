using Microsoft.EntityFrameworkCore;

using Ovrilo.BuildingBlocks;
using Ovrilo.BuildingBlocks.Realtime;
using Ovrilo.Modules.Messaging.Persistence;

namespace Ovrilo.Modules.Messaging.Features;

/// <summary>
/// The account leaves its groups and its read positions go. Messages stay and show as from
/// "Deleted user"; direct conversations stay readable for the other person.
/// </summary>
internal sealed class MessagingAccountDeletion(MessagingDbContext db, IRealtimePublisher realtime) : IAccountDeletionParticipant
{
    public async Task DeleteAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await GroupEndpoints.LeaveAllAsync(db, realtime, userId, cancellationToken);
        await db.ReadStates.Where(r => r.UserId == userId).ExecuteDeleteAsync(cancellationToken);
    }
}
