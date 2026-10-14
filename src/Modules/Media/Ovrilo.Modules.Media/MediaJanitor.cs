using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Ovrilo.BuildingBlocks.Persistence;
using Ovrilo.Modules.Media.Domain;
using Ovrilo.Modules.Media.Features;
using Ovrilo.Modules.Media.Persistence;
using Ovrilo.Modules.Media.Storage;

namespace Ovrilo.Modules.Media;

/// <summary>
/// Reclaims storage (docs/domain/media.md, lifecycle): abandoned uploads, verified uploads nobody used, and
/// objects of rejected or released assets. PostgreSQL and object storage share no transaction, so every step
/// is idempotent and a failed object deletion is retried on the next run.
/// </summary>
internal sealed partial class MediaJanitor(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<MediaJanitor> logger) : BackgroundService
{
    private const int BatchSize = 100;

    /// <summary>Extra time after an upload's completion window before it counts as abandoned.</summary>
    private static readonly TimeSpan AbandonedGrace = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Media:Janitor:Enabled", true))
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(configuration.GetValue("Media:Janitor:IntervalSeconds", 300));
        using var timer = new PeriodicTimer(interval, timeProvider);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRunFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One cleanup pass. Returns the number of stored objects deleted.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MediaDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorage>();
        var limits = scope.ServiceProvider.GetRequiredService<IOptions<MediaLimits>>().Value;
        var now = timeProvider.GetDatabaseUtcNow();

        var abandonedBefore = now - TimeSpan.FromSeconds(limits.CompleteGraceSeconds) - AbandonedGrace;
        await db.Assets
            .Where(a => a.Status == MediaAssetStatus.Pending && a.UploadExpiresAt < abandonedBefore)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, MediaAssetStatus.Deleted).SetProperty(a => a.DeletedAt, now), cancellationToken);

        var unusedBefore = now - TimeSpan.FromHours(limits.UnattachedRetentionHours);
        await db.Assets
            .Where(a => a.Status == MediaAssetStatus.Ready && a.AttachedAt == null && a.UploadedAt < unusedBefore)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, MediaAssetStatus.Deleted).SetProperty(a => a.DeletedAt, now), cancellationToken);

        var purged = 0;
        while (true)
        {
            var batch = await db.Assets
                .Where(a => (a.Status == MediaAssetStatus.Deleted || a.Status == MediaAssetStatus.Rejected) && a.ObjectPurgedAt == null)
                .OrderBy(a => a.CreatedAt)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            foreach (var asset in batch)
            {
                await storage.DeleteAsync(asset.ObjectKey, cancellationToken);
                asset.ObjectPurgedAt = now;
                purged++;
            }

            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }

        return purged;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Media cleanup run failed; it is retried on the next run.")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);
}
