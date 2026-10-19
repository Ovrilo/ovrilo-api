using Microsoft.AspNetCore.SignalR;

using Ovrilo.BuildingBlocks.Realtime;
using Ovrilo.Modules.Communities.Contracts;
using Ovrilo.Modules.Identity.Contracts;
using Ovrilo.Modules.Social.Contracts;

namespace Ovrilo.Api.Realtime;

/// <summary>
/// Sent to the user's communities and friends when what they see changes. <c>Status</c> is
/// online, away or doNotDisturb while <c>Online</c>; null when offline (or invisible).
/// <c>Activity</c> is what they are doing, such as the game they play, or null.
/// </summary>
public sealed record PresenceChangedEvent(Guid UserId, bool Online, Availability? Status = null, UserActivity? Activity = null);

/// <summary>A user others see as connected, how, and what they are doing.</summary>
public sealed record UserPresence(Guid UserId, Availability Status, UserActivity? Activity = null);

public enum ActivityType
{
    Playing,
}

/// <summary>What a user is doing: for now, the game they play.</summary>
public sealed record UserActivity(ActivityType Type, string Name)
{
    public const int NameMaxLength = 128;

    /// <summary>A shareable game name: trimmed, without control characters, at most 128 characters; null when empty.</summary>
    public static UserActivity? Playing(string? name)
    {
        var clean = new string([.. (name ?? string.Empty).Where(c => !char.IsControl(c))]).Trim();
        if (clean.Length == 0)
        {
            return null;
        }

        return new UserActivity(ActivityType.Playing, clean.Length > NameMaxLength ? clean[..NameMaxLength].TrimEnd() : clean);
    }
}

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

    /// <summary>What others currently see: status and activity, or null when offline or invisible.</summary>
    public UserPresence? PresenceOf(Guid userId)
    {
        lock (gate)
        {
            return users.TryGetValue(userId, out var entry) && entry.Published is { } shown
                ? new UserPresence(userId, shown.Status, shown.Activity)
                : null;
        }
    }

    public bool IsOnline(Guid userId) => PresenceOf(userId) is not null;

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
            entry.Activities.Remove(connectionId);
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

    /// <summary>What a connection's device is doing (the game it plays), or null; the latest across devices is shown.</summary>
    public Task SetActivityAsync(Guid userId, string connectionId, UserActivity? activity) =>
        UpdateAsync(userId, entry =>
        {
            if (!entry.Connections.Contains(connectionId))
            {
                return;
            }

            if (activity is null)
            {
                entry.Activities.Remove(connectionId);
            }
            else if (!entry.Activities.TryGetValue(connectionId, out var current) || current.Activity != activity)
            {
                entry.Activities[connectionId] = (activity, ++entry.ActivityClock);
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
        Shown? now;
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

    private static Shown? Visible(Entry entry)
    {
        Availability? status = entry.Chosen switch
        {
            Availability.Invisible => null,
            Availability.DoNotDisturb => Availability.DoNotDisturb,
            Availability.Away => Availability.Away,
            _ when entry.Idle.Count > 0 && entry.Idle.Count == entry.Connections.Count => Availability.Away,
            _ => Availability.Online,
        };
        if (status is not { } visible)
        {
            return null;
        }

        // The device that reported most recently wins.
        var activity = entry.Activities.Values.OrderByDescending(a => a.Order).Select(a => a.Activity).FirstOrDefault();
        return new Shown(visible, activity);
    }

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
            Shown? published;
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

    private async Task PublishAsync(Guid userId, Shown? shown)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var change = new PresenceChangedEvent(userId, shown is not null, shown?.Status, shown?.Activity);
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

        /// <summary>What each connection's device is doing, with the order it was reported in.</summary>
        public Dictionary<string, (UserActivity Activity, long Order)> Activities { get; } = [];

        public long ActivityClock { get; set; }

        /// <summary>What others were last told, or null for offline.</summary>
        public Shown? Published { get; set; }
    }

    /// <summary>What others see of a connected user. Records compare by value, so changes are detected.</summary>
    private sealed record Shown(Availability Status, UserActivity? Activity);
}
