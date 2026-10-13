using System.Security.Claims;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

using Ovrilo.BuildingBlocks;
using Ovrilo.BuildingBlocks.Persistence;
using Ovrilo.Modules.Channels.Contracts;
using Ovrilo.Modules.Channels.Domain;
using Ovrilo.Modules.Channels.Persistence;
using Ovrilo.Modules.Communities.Contracts;
using Ovrilo.Modules.Identity.Contracts;

namespace Ovrilo.Modules.Channels.Features;

public sealed record CreateChannelRequest(string Name, ChannelType Type, string? Topic);

/// <summary>Partial update; omitted (null) fields are unchanged. An empty topic clears it.</summary>
public sealed record UpdateChannelRequest(string? Name, string? Topic, int? Position);

public sealed record ChannelResponse(
    Guid Id,
    Guid CommunityId,
    string Name,
    ChannelType Type,
    string? Topic,
    int Position,
    DateTimeOffset CreatedAt);

internal static class ChannelProblems
{
    /// <summary>Also returned when the caller cannot view the channel or community, so existence is not revealed.</summary>
    public static ProblemHttpResult NotFound() =>
        ApiProblems.Create(StatusCodes.Status404NotFound, "channels.not_found", "Channel not found.");

    public static ProblemHttpResult CommunityNotFound() =>
        ApiProblems.Create(StatusCodes.Status404NotFound, "communities.not_found", "Community not found.");

    public static ProblemHttpResult PermissionDenied() =>
        ApiProblems.Create(StatusCodes.Status403Forbidden, "channels.permission_denied", "You do not have permission to manage channels.");

    public static ProblemHttpResult NameTaken() =>
        ApiProblems.Create(StatusCodes.Status409Conflict, "channels.name_taken", "A channel of this type with this name already exists.");
}

internal static class ChannelEndpoints
{
    public static async Task<Results<Ok<List<ChannelResponse>>, ProblemHttpResult>> ListAsync(
        Guid communityId,
        ClaimsPrincipal principal,
        ChannelsDbContext db,
        ICommunitiesModule communities,
        CancellationToken cancellationToken)
    {
        var permissions = await communities.GetPermissionsAsync(communityId, principal.GetRequiredUserId(), cancellationToken);
        if (!permissions.HasFlag(CommunityPermissions.ViewChannels))
        {
            return ChannelProblems.CommunityNotFound();
        }

        var channels = await db.Channels.AsNoTracking()
            .Where(c => c.CommunityId == communityId && c.DeletedAt == null)
            .OrderBy(c => c.Type).ThenBy(c => c.Position).ThenBy(c => c.CreatedAt)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(channels.Select(ToResponse).ToList());
    }

    public static async Task<Results<Created<ChannelResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        Guid communityId,
        CreateChannelRequest request,
        ClaimsPrincipal principal,
        ChannelsDbContext db,
        ICommunitiesModule communities,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var userId = principal.GetRequiredUserId();
        var permissions = await communities.GetPermissionsAsync(communityId, userId, cancellationToken);
        if (!permissions.HasFlag(CommunityPermissions.ViewChannels))
        {
            return ChannelProblems.CommunityNotFound();
        }

        if (!permissions.HasFlag(CommunityPermissions.ManageChannels))
        {
            return ChannelProblems.PermissionDenied();
        }

        var name = NormalizeName(request.Name);
        var topic = NormalizeTopic(request.Topic);
        var errors = new Dictionary<string, string[]>();
        ValidateName(name, errors);
        ValidateTopic(topic, errors);
        if (!Enum.IsDefined(request.Type))
        {
            errors["type"] = ["Type must be 'text' or 'voice'."];
        }

        if (errors.Count > 0)
        {
            return ApiProblems.Validation(errors);
        }

        var now = timeProvider.GetDatabaseUtcNow();
        var nextPosition = await db.Channels
            .Where(c => c.CommunityId == communityId && c.Type == request.Type && c.DeletedAt == null)
            .MaxAsync(c => (int?)c.Position, cancellationToken) ?? -1;

        var channel = new Channel
        {
            Id = Guid.CreateVersion7(now),
            CommunityId = communityId,
            Name = name,
            NormalizedName = name.ToLowerInvariant(),
            Type = request.Type,
            Topic = topic,
            Position = nextPosition + 1,
            CreatedBy = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Channels.Add(channel);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation(ChannelsDbContext.UniqueActiveNameIndex))
        {
            return ChannelProblems.NameTaken();
        }

        return TypedResults.Created($"/channels/{channel.Id}", ToResponse(channel));
    }

    public static async Task<Results<Ok<ChannelResponse>, ProblemHttpResult>> GetAsync(
        Guid channelId,
        ClaimsPrincipal principal,
        ChannelsDbContext db,
        ICommunitiesModule communities,
        CancellationToken cancellationToken)
    {
        var channel = await db.Channels.AsNoTracking().SingleOrDefaultAsync(c => c.Id == channelId && c.DeletedAt == null, cancellationToken);
        if (channel is null
            || !(await communities.GetPermissionsAsync(channel.CommunityId, principal.GetRequiredUserId(), cancellationToken)).HasFlag(CommunityPermissions.ViewChannels))
        {
            return ChannelProblems.NotFound();
        }

        return TypedResults.Ok(ToResponse(channel));
    }

    public static async Task<Results<Ok<ChannelResponse>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid channelId,
        UpdateChannelRequest request,
        ClaimsPrincipal principal,
        ChannelsDbContext db,
        ICommunitiesModule communities,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var channel = await db.Channels.SingleOrDefaultAsync(c => c.Id == channelId && c.DeletedAt == null, cancellationToken);
        var denied = await AuthorizeManageAsync(channel, principal, communities, cancellationToken);
        if (denied is not null)
        {
            return denied;
        }

        var errors = new Dictionary<string, string[]>();
        if (request.Name is not null)
        {
            var name = NormalizeName(request.Name);
            ValidateName(name, errors);
            channel!.Rename(name);
        }

        if (request.Topic is not null)
        {
            var topic = NormalizeTopic(request.Topic);
            ValidateTopic(topic, errors);
            channel!.Topic = topic;
        }

        if (request.Position is { } position)
        {
            if (position < 0)
            {
                errors["position"] = ["Position must be zero or greater."];
            }

            channel!.Position = position;
        }

        if (errors.Count > 0)
        {
            return ApiProblems.Validation(errors);
        }

        channel!.UpdatedAt = timeProvider.GetDatabaseUtcNow();
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation(ChannelsDbContext.UniqueActiveNameIndex))
        {
            return ChannelProblems.NameTaken();
        }

        return TypedResults.Ok(ToResponse(channel));
    }

    /// <summary>Soft-deletes the channel. Message retention and active voice ejection are coordinated in later phases.</summary>
    public static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid channelId,
        ClaimsPrincipal principal,
        ChannelsDbContext db,
        ICommunitiesModule communities,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var channel = await db.Channels.SingleOrDefaultAsync(c => c.Id == channelId && c.DeletedAt == null, cancellationToken);
        var denied = await AuthorizeManageAsync(channel, principal, communities, cancellationToken);
        if (denied is not null)
        {
            return denied;
        }

        var now = timeProvider.GetDatabaseUtcNow();
        channel!.DeletedAt = now;
        channel.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<ProblemHttpResult?> AuthorizeManageAsync(
        Channel? channel,
        ClaimsPrincipal principal,
        ICommunitiesModule communities,
        CancellationToken cancellationToken)
    {
        if (channel is null)
        {
            return ChannelProblems.NotFound();
        }

        var permissions = await communities.GetPermissionsAsync(channel.CommunityId, principal.GetRequiredUserId(), cancellationToken);
        if (!permissions.HasFlag(CommunityPermissions.ViewChannels))
        {
            return ChannelProblems.NotFound();
        }

        return permissions.HasFlag(CommunityPermissions.ManageChannels) ? null : ChannelProblems.PermissionDenied();
    }

    private static string NormalizeName(string? name) => (name ?? string.Empty).Trim();

    private static string? NormalizeTopic(string? topic) => string.IsNullOrWhiteSpace(topic) ? null : topic.Trim();

    private static void ValidateName(string name, Dictionary<string, string[]> errors)
    {
        if (name.Length is 0 or > Channel.NameMaxLength || name.Any(char.IsControl))
        {
            errors["name"] = [$"Name must be 1-{Channel.NameMaxLength} characters without control characters."];
        }
    }

    private static void ValidateTopic(string? topic, Dictionary<string, string[]> errors)
    {
        if (topic is not null && (topic.Length > Channel.TopicMaxLength || topic.Any(c => char.IsControl(c) && c is not '\n')))
        {
            errors["topic"] = [$"Topic must be at most {Channel.TopicMaxLength} characters."];
        }
    }

    private static ChannelResponse ToResponse(Channel channel) =>
        new(channel.Id, channel.CommunityId, channel.Name, channel.Type, channel.Topic, channel.Position, channel.CreatedAt);
}
