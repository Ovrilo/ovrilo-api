using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Ovrilo.BuildingBlocks.Persistence;
using Ovrilo.Modules.Identity.Contracts;
using Ovrilo.Modules.Media.Contracts;
using Ovrilo.Modules.Media.Domain;
using Ovrilo.Modules.Media.Features;
using Ovrilo.Modules.Media.Persistence;
using Ovrilo.Modules.Media.Storage;

namespace Ovrilo.Modules.Media;

/// <summary>
/// Composition entry points for the Media module (docs/domain/media.md, ADR-0014, ADR-0015).
/// Metadata in PostgreSQL; bytes in S3-compatible storage behind <see cref="IObjectStorage"/>.
/// </summary>
public static class MediaModule
{
    /// <summary>Per-user limit on starting uploads.</summary>
    public const string UploadRateLimitPolicy = "media-upload";

    private const string RateLimitSection = "Media:RateLimit";

    public static IServiceCollection AddMediaModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<MediaDbContext>(MediaDbContext.Schema);
        services.AddOptions<StorageOptions>()
            .BindConfiguration(StorageOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<MediaLimits>().BindConfiguration(MediaLimits.SectionName);
        services.AddSingleton<IObjectStorage, S3ObjectStorage>();
        services.AddScoped<IMediaModule, MediaModuleApi>();
        services.AddSingleton<MediaJanitor>();
        services.AddHostedService(provider => provider.GetRequiredService<MediaJanitor>());

        services.AddRateLimiter(options => options.AddPolicy(UploadRateLimitPolicy, context =>
        {
            var section = context.RequestServices.GetRequiredService<IConfiguration>().GetSection(RateLimitSection);
            var user = context.User.FindFirst(IdentityClaims.UserId)?.Value ?? "anonymous";
            return RateLimitPartition.GetFixedWindowLimiter(user, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = section.GetValue("PermitLimit", 20),
                Window = TimeSpan.FromSeconds(section.GetValue("WindowSeconds", 60)),
                QueueLimit = 0,
            });
        }));
        return services;
    }

    public static IEndpointRouteBuilder MapMediaEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var media = endpoints.MapGroup("/media").WithTags("Media").RequireAuthorization();

        media.MapPost("/uploads", MediaEndpoints.CreateUploadAsync)
            .RequireRateLimiting(UploadRateLimitPolicy)
            .WithName("CreateMediaUpload")
            .WithSummary("Starts an image upload and returns a short-lived URL to PUT the file to. Call complete afterwards.")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        media.MapPost("/uploads/{assetId:guid}/complete", MediaEndpoints.CompleteUploadAsync)
            .WithName("CompleteMediaUpload")
            .WithSummary("Verifies an uploaded image (size, real format, dimensions) and makes it usable. Safe to retry.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        media.MapPost("/download-urls", MediaEndpoints.CreateDownloadUrlsAsync)
            .WithName("CreateMediaDownloadUrls")
            .WithSummary("Short-lived read URLs for up to 100 images the caller may view (avatars, icons of their communities).");

        return endpoints;
    }

    public static Task MigrateMediaDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default) =>
        services.MigrateModuleAsync<MediaDbContext>(cancellationToken);
}

internal sealed class MediaModuleApi(MediaDbContext db, TimeProvider timeProvider) : IMediaModule
{
    public async Task<MediaAssetInfo?> AttachAsync(Guid assetId, Guid uploadedBy, MediaPurpose purpose, CancellationToken cancellationToken = default)
    {
        var asset = await db.Assets.SingleOrDefaultAsync(
            a => a.Id == assetId && a.OwnerId == uploadedBy && a.Purpose == purpose && a.Status == MediaAssetStatus.Ready,
            cancellationToken);
        if (asset is null)
        {
            return null;
        }

        if (asset.AttachedAt is null)
        {
            asset.AttachedAt = timeProvider.GetDatabaseUtcNow();
            await db.SaveChangesAsync(cancellationToken);
        }

        return new MediaAssetInfo(asset.Id, asset.OwnerId, asset.Purpose, asset.CommunityId, asset.DetectedContentType!);
    }

    public async Task ReleaseAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetDatabaseUtcNow();
        await db.Assets
            .Where(a => a.Id == assetId && a.Status != MediaAssetStatus.Deleted && a.Status != MediaAssetStatus.Rejected)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, MediaAssetStatus.Deleted).SetProperty(a => a.DeletedAt, now), cancellationToken);
    }
}
