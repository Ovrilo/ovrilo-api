using System.Security.Claims;
using System.Security.Cryptography;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Ovrilo.BuildingBlocks;
using Ovrilo.BuildingBlocks.Persistence;
using Ovrilo.Modules.Communities.Contracts;
using Ovrilo.Modules.Identity.Contracts;
using Ovrilo.Modules.Media.Contracts;
using Ovrilo.Modules.Media.Domain;
using Ovrilo.Modules.Media.Images;
using Ovrilo.Modules.Media.Persistence;
using Ovrilo.Modules.Media.Storage;

namespace Ovrilo.Modules.Media.Features;

internal static class MediaProblems
{
    /// <summary>Also returned for another user's upload.</summary>
    public static ProblemHttpResult NotFound() =>
        ApiProblems.Create(StatusCodes.Status404NotFound, "media.not_found", "Upload not found.");

    /// <summary>Also returned when the caller is not a member, so community existence is not revealed.</summary>
    public static ProblemHttpResult CommunityNotFound() =>
        ApiProblems.Create(StatusCodes.Status404NotFound, "communities.not_found", "Community not found.");

    public static ProblemHttpResult PermissionDenied() =>
        ApiProblems.Create(StatusCodes.Status403Forbidden, "media.permission_denied", "You cannot change this community's icon.");

    public static ProblemHttpResult TooManyPending() =>
        ApiProblems.Create(StatusCodes.Status429TooManyRequests, "media.too_many_pending", "Finish or wait for your other uploads first.");

    public static ProblemHttpResult UploadMissing() =>
        ApiProblems.Create(StatusCodes.Status409Conflict, "media.upload_missing", "The file has not been uploaded yet.");

    public static ProblemHttpResult UploadExpired() =>
        ApiProblems.Create(StatusCodes.Status409Conflict, "media.upload_expired", "The upload took too long. Please try again.");

    public static ProblemHttpResult Unavailable() =>
        ApiProblems.Create(StatusCodes.Status503ServiceUnavailable, "media.unavailable", "Pictures are not available on this server yet.");

    public static ProblemHttpResult Rejected(string title) =>
        ApiProblems.Create(StatusCodes.Status422UnprocessableEntity, "media.invalid_image", title);
}

internal static class MediaEndpoints
{
    public const int MaxDownloadBatch = 100;

    public static async Task<Results<Created<UploadResponse>, ValidationProblem, ProblemHttpResult>> CreateUploadAsync(
        CreateUploadRequest request,
        ClaimsPrincipal principal,
        MediaDbContext db,
        IObjectStorage storage,
        ICommunitiesModule communities,
        IOptions<MediaLimits> limitsOptions,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!storage.IsAvailable)
        {
            return MediaProblems.Unavailable();
        }

        var userId = principal.GetRequiredUserId();
        var limits = limitsOptions.Value;

        var errors = new Dictionary<string, string[]>();
        if (!Enum.IsDefined(request.Purpose))
        {
            errors["purpose"] = ["Unknown purpose."];
        }

        if (!ImageInspector.SupportedContentTypes.Contains(request.ContentType ?? ""))
        {
            errors["contentType"] = ["Use a PNG, JPEG, GIF or WebP image."];
        }

        if (request.SizeBytes <= 0)
        {
            errors["sizeBytes"] = ["The file is empty."];
        }
        else if (request.SizeBytes > limits.ImageMaxBytes)
        {
            errors["sizeBytes"] = [$"Images can be at most {limits.ImageMaxBytes / (1024 * 1024)} MB."];
        }

        var filename = string.IsNullOrWhiteSpace(request.Filename) ? null : request.Filename.Trim();
        if (filename is { Length: > MediaAsset.OriginalFilenameMaxLength })
        {
            filename = filename[..MediaAsset.OriginalFilenameMaxLength];
        }

        if (request.Purpose == MediaPurpose.CommunityIcon && request.CommunityId is null)
        {
            errors["communityId"] = ["A community is required for a community icon."];
        }
        else if (request.Purpose != MediaPurpose.CommunityIcon && request.CommunityId is not null)
        {
            errors["communityId"] = ["Only community icons belong to a community."];
        }

        if (errors.Count > 0)
        {
            return ApiProblems.Validation(errors);
        }

        if (request.CommunityId is { } communityId)
        {
            var permissions = await communities.GetPermissionsAsync(communityId, userId, cancellationToken);
            if (permissions == CommunityPermissions.None)
            {
                return MediaProblems.CommunityNotFound();
            }

            if (!permissions.HasFlag(CommunityPermissions.ManageCommunity))
            {
                return MediaProblems.PermissionDenied();
            }
        }

        var now = timeProvider.GetDatabaseUtcNow();
        var pending = await db.Assets.CountAsync(
            a => a.OwnerId == userId && a.Status == MediaAssetStatus.Pending && a.UploadExpiresAt > now,
            cancellationToken);
        if (pending >= limits.MaxPendingUploadsPerUser)
        {
            return MediaProblems.TooManyPending();
        }

        var lifetime = TimeSpan.FromSeconds(limits.UploadUrlLifetimeSeconds);
        var asset = new MediaAsset
        {
            Id = Guid.CreateVersion7(),
            OwnerId = userId,
            Purpose = request.Purpose,
            CommunityId = request.CommunityId,
            Bucket = storage.Bucket,
            ObjectKey = NewObjectKey(request.Purpose),
            OriginalFilename = filename,
            DeclaredContentType = request.ContentType!,
            MaxBytes = limits.ImageMaxBytes,
            UploadExpiresAt = now + lifetime,
            Status = MediaAssetStatus.Pending,
            CreatedAt = now,
        };
        db.Assets.Add(asset);
        await db.SaveChangesAsync(cancellationToken);

        var url = await storage.CreateUploadUrlAsync(asset.ObjectKey, asset.DeclaredContentType, lifetime, cancellationToken);
        return TypedResults.Created(
            $"/media/uploads/{asset.Id}",
            new UploadResponse(asset.Id, url, asset.DeclaredContentType, asset.MaxBytes, asset.UploadExpiresAt));
    }

    public static async Task<Results<Ok<MediaAssetResponse>, ProblemHttpResult>> CompleteUploadAsync(
        Guid assetId,
        ClaimsPrincipal principal,
        MediaDbContext db,
        IObjectStorage storage,
        IOptions<MediaLimits> limitsOptions,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!storage.IsAvailable)
        {
            return MediaProblems.Unavailable();
        }

        var userId = principal.GetRequiredUserId();
        var limits = limitsOptions.Value;
        var asset = await db.Assets.SingleOrDefaultAsync(a => a.Id == assetId && a.OwnerId == userId, cancellationToken);
        if (asset is null || asset.Status is MediaAssetStatus.Deleted)
        {
            return MediaProblems.NotFound();
        }

        switch (asset.Status)
        {
            case MediaAssetStatus.Ready:
                // Retried completion.
                return TypedResults.Ok(ToResponse(asset));
            case MediaAssetStatus.Rejected:
                return MediaProblems.Rejected("This file is not a supported image.");
        }

        var now = timeProvider.GetDatabaseUtcNow();
        if (now > asset.UploadExpiresAt.AddSeconds(limits.CompleteGraceSeconds))
        {
            return MediaProblems.UploadExpired();
        }

        var stored = await storage.GetMetadataAsync(asset.ObjectKey, cancellationToken);
        if (stored is null)
        {
            return MediaProblems.UploadMissing();
        }

        // Presigned PUTs cannot cap the size, so verify after the fact and never trust the declared type.
        string? rejection = null;
        ImageInfo? image = null;
        if (stored.SizeBytes > asset.MaxBytes)
        {
            rejection = $"Images can be at most {asset.MaxBytes / (1024 * 1024)} MB.";
        }
        else
        {
            var header = await storage.ReadPrefixAsync(asset.ObjectKey, ImageInspector.HeaderBytes, cancellationToken);
            image = ImageInspector.Inspect(header);
            if (image is null || image.ContentType != asset.DeclaredContentType)
            {
                rejection = "This file is not a supported image.";
            }
            else if (image.Width > limits.ImageMaxDimension || image.Height > limits.ImageMaxDimension)
            {
                rejection = $"Images can be at most {limits.ImageMaxDimension}×{limits.ImageMaxDimension} pixels.";
            }
        }

        if (rejection is not null)
        {
            // The janitor deletes the object.
            asset.Status = MediaAssetStatus.Rejected;
            asset.SizeBytes = stored.SizeBytes;
            asset.DeletedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            return MediaProblems.Rejected(rejection);
        }

        asset.Status = MediaAssetStatus.Ready;
        asset.DetectedContentType = image!.ContentType;
        asset.SizeBytes = stored.SizeBytes;
        asset.Width = image.Width;
        asset.Height = image.Height;
        asset.UploadedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(ToResponse(asset));
    }

    public static async Task<Results<Ok<DownloadUrlsResponse>, ValidationProblem, ProblemHttpResult>> CreateDownloadUrlsAsync(
        DownloadUrlsRequest request,
        ClaimsPrincipal principal,
        MediaDbContext db,
        IObjectStorage storage,
        ICommunitiesModule communities,
        IOptions<MediaLimits> limitsOptions,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!storage.IsAvailable)
        {
            return MediaProblems.Unavailable();
        }

        var ids = (request.AssetIds ?? []).Distinct().ToList();
        if (ids.Count > MaxDownloadBatch)
        {
            return ApiProblems.Validation(new Dictionary<string, string[]>
            {
                ["assetIds"] = [$"Request at most {MaxDownloadBatch} assets at once."],
            });
        }

        var userId = principal.GetRequiredUserId();
        var assets = ids.Count == 0
            ? []
            : await db.Assets.AsNoTracking()
                .Where(a => ids.Contains(a.Id) && a.Status == MediaAssetStatus.Ready)
                .ToListAsync(cancellationToken);

        var lifetime = TimeSpan.FromSeconds(limitsOptions.Value.DownloadUrlLifetimeSeconds);
        var expiresAt = timeProvider.GetUtcNow() + lifetime;
        var memberOf = new Dictionary<Guid, bool>();
        var urls = new List<DownloadUrl>(assets.Count);
        foreach (var asset in assets)
        {
            if (asset.Purpose == MediaPurpose.CommunityIcon && asset.CommunityId is { } communityId)
            {
                if (!memberOf.TryGetValue(communityId, out var isMember))
                {
                    isMember = await communities.GetPermissionsAsync(communityId, userId, cancellationToken) != CommunityPermissions.None;
                    memberOf[communityId] = isMember;
                }

                if (!isMember)
                {
                    continue;
                }
            }

            // Avatars are profile data, like display names: any signed-in user may see them.
            var contentType = asset.DetectedContentType!;
            var url = await storage.CreateDownloadUrlAsync(asset.ObjectKey, contentType, lifetime, cancellationToken);
            urls.Add(new DownloadUrl(asset.Id, url, contentType, expiresAt));
        }

        return TypedResults.Ok(new DownloadUrlsResponse(urls));
    }

    private static MediaAssetResponse ToResponse(MediaAsset asset) =>
        new(asset.Id, asset.Purpose, asset.DetectedContentType!, asset.SizeBytes!.Value, asset.Width!.Value, asset.Height!.Value);

    /// <summary>Unpredictable key with a purpose prefix for operations (lifecycle rules, audits); no user data in it.</summary>
    private static string NewObjectKey(MediaPurpose purpose)
    {
        var prefix = purpose switch
        {
            MediaPurpose.Avatar => "avatars",
            MediaPurpose.CommunityIcon => "community-icons",
            _ => "other",
        };
        return $"{prefix}/{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))}";
    }
}
