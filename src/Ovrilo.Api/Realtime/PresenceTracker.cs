using Microsoft.AspNetCore.SignalR;

using Ovrilo.BuildingBlocks.Realtime;
using Ovrilo.Modules.Communities.Contracts;
using Ovrilo.Modules.Identity.Contracts;
using Ovrilo.Modules.Social.Contracts;

namespace Ovrilo.Api.Realtime;

/// <summary>
/// Sent to the user's communities and friends when what they see changes. <c>Status</c> is
/// online, away or doNotDisturb while <c>Online</c>; null when offline (or invisible).
/// </summary>
public sealed record PresenceChangedEvent(Guid UserId, bool Online, Availability? Status = null);

/// <summary>A user others see as connected, and how.</summary>
public sealed record UserPresence(Guid UserId, Availability Status);

/// <summary>
/// Presence from open realtime connections and the user's chosen status. A user is online while at
/// least one connection is open, unless they chose Invisible. They show as Away when they chose it
/// or every connection reports the computer idle. Going offline waits a grace period so reconnects
/// (network blips, token refresh closes) do not flicker. State is per API instance: a scale-out
/// needs a shared store (see contracts/events).
/// </summary>
public sealed class PresenceTracker(
    IHubContext<RealtimeHub> hub,
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<PresenceTracker> logger)
{
    public const string PresenceChanged = nameof(PresenceChanged);

    private readonly Dictionary<Guid, Entry> users = [];
    private readonly Lock gate = new();

    private TimeSpan Grace => TimeSpan.FromSeconds(configuration.GetValue("Realtime:PresenceGraceSeconds", 10.0));

    /// <summary>What others currently see: the status, or null when offline or invisible.</summary>
    public Availability? StatusOf(Guid userId)
    {
        lock (gate)
        {
            return users.TryGetValue(userId, out var entry) ? entry.Published : null;
        }
    }

    public bool IsOnline(Guid userId) => StatusOf(userId) is not null;

    public async Task ConnectedAsync(Guid userId, string connectionId)
    {
        bool known;
        lock (gate)
        {
            known = users.TryGetValue(userId, out var entry) && entry.Loaded;
        }

        // The stored choice is read once per online period, outside the lock.
        var chosen = known ? (Availability?)null : await LoadAvailabilityAsync(userId);
        await UpdateAsync(userId, entry =>
        {
            entry.Connections.Add(connectionId);
            if (chosen is { } value && !entry.Loaded)
            {
                entry.Chosen = value;
                entry.Loaded = true;
            }
        });
    }

    public void Disconnected(Guid userId, string connectionId)
    {
        bool gone;
        lock (gate)
        {
            if (!users.TryGetValue(userId, out var entry))
            {
                return;
            }

            entry.Connections.Remove(connectionId);
            entry.Idle.Remove(connectionId);
            gone = entry.Connections.Count == 0;
        }

        // Detached on purpose: the disconnecting connection must not wait for the grace period.
        _ = gone ? GoOfflineAfterGraceAsync(userId) : UpdateAsync(userId, _ => { });
    }

    /// <summary>A connection reports whether its computer is idle (no input for a while).</summary>
    public Task SetIdleAsync(Guid userId, string connectionId, bool idle) =>
        UpdateAsync(userId, entry =>
        {
            if (!entry.Connections.Contains(connectionId))
            {
                return;
            }

            if (idle)
            {
                entry.Idle.Add(connectionId);
            }
            else
            {
                entry.Idle.Remove(connectionId);
            }
        });

    /// <summary>The user chose a new status (already stored); others see it right away.</summary>
    public Task ChoseAsync(Guid userId, Availability chosen) =>
        UpdateAsync(userId, entry =>
        {
            entry.Chosen = chosen;
            entry.Loaded = true;
        });

    /// <summary>Applies a change to a connected user's entry and publishes what others now see, if it changed.</summary>
    private async Task UpdateAsync(Guid userId, Action<Entry> change)
    {
        Availability? now;
        lock (gate)
        {
            var created = !users.TryGetValue(userId, out var entry);
            if (created)
            {
                entry = users[userId] = new Entry();
            }

            change(entry!);
            if (entry!.Connections.Count == 0)
            {
                // Not connected (or leaving): the grace timer decides about going offline, and a
                // user who never connected is not kept around.
                if (created)
                {
                    users.Remove(userId);
                }

                return;
            }

            now = Visible(entry);
            if (now == entry.Published)
            {
                return;
            }

            entry.Published = now;
        }

        await PublishAsync(userId, now);
    }

    private static Availability? Visible(Entry entry) => entry.Chosen switch
    {
        Availability.Invisible => null,
        Availability.DoNotDisturb => Availability.DoNotDisturb,
        Availability.Away => Availability.Away,
        _ when entry.Idle.Count > 0 && entry.Idle.Count == entry.Connections.Count => Availability.Away,
        _ => Availability.Online,
    };

    private async Task<Availability> LoadAvailabilityAsync(Guid userId)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IIdentityModule>().GetAvailabilityAsync(userId);
        }
        catch (Exception ex)
        {
            Log.PresenceFailed(logger, userId, ex);
            return Availability.Online;
        }
    }

    private async Task GoOfflineAfterGraceAsync(Guid userId)
    {
        try
        {
            await Task.Delay(Grace, time);
            Availability? published;
            lock (gate)
            {
                if (!users.TryGetValue(userId, out var entry) || entry.Connections.Count > 0)
                {
                    return;
                }

                users.Remove(userId);
                published = entry.Published;
            }

            if (published is not null)
            {
                await PublishAsync(userId, null);
            }
        }
        catch (Exception ex)
        {
            Log.PresenceFailed(logger, userId, ex);
        }
    }

    private async Task PublishAsync(Guid userId, Availability? status)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var change = new PresenceChangedEvent(userId, status is not null, status);
            var communities = await scope.ServiceProvider.GetRequiredService<ICommunitiesModule>().ListCommunityIdsAsync(userId);
            if (communities.Count > 0)
            {
                await hub.Clients.Groups([.. communities.Select(RealtimeGroups.Community)])
                    .SendAsync(PresenceChanged, change);
            }

            // Friends see it too, also without a shared community (a friend in one may get it twice).
            var friends = await scope.ServiceProvider.GetRequiredService<ISocialModule>().ListFriendIdsAsync(userId);
            if (friends.Count > 0)
            {
                await hub.Clients.Users([.. friends.Select(id => id.ToString())]).SendAsync(PresenceChanged, change);
            }
        }
        catch (Exception ex)
        {
            // Best effort like other events: clients take a fresh snapshot when they (re)subscribe.
            Log.PresenceFailed(logger, userId, ex);
        }
    }

    private sealed class Entry
    {
        public HashSet<string> Connections { get; } = [];

        /// <summary>Connections whose computer reported no input for a while.</summary>
        public HashSet<string> Idle { get; } = [];

        public Availability Chosen { get; set; }

        /// <summary>Whether <see cref="Chosen"/> was read from the account yet.</summary>
        public bool Loaded { get; set; }

        /// <summary>What others were last told: a status, or null for offline.</summary>
        public Availability? Published { get; set; }
    }
}
