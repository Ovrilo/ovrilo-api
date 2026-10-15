using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Ovrilo.BuildingBlocks.Persistence;
using Ovrilo.Modules.Channels.Contracts;
using Ovrilo.Modules.Channels.Domain;
using Ovrilo.Modules.Channels.Features;
using Ovrilo.Modules.Channels.Persistence;
using Ovrilo.Modules.Communities.Contracts;

namespace Ovrilo.Modules.Channels;

/// <summary>Composition entry points for the Channels module. Other modules use <see cref="IChannelsModule"/>.</summary>
public static class ChannelsModule
{
    public static IServiceCollection AddChannelsModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<ChannelsDbContext>(ChannelsDbContext.Schema);
        services.AddScoped<IChannelsModule, ChannelsModuleApi>();
        return services;
    }

    public static IEndpointRouteBuilder MapChannelsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/communities/{communityId:guid}/channels", ChannelEndpoints.ListAsync)
            .RequireAuthorization()
            .WithTags("Channels")
            .WithName("ListChannels")
            .WithSummary("Lists channels the caller can view, ordered by type and position.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapPost("/communities/{communityId:guid}/channels", ChannelEndpoints.CreateAsync)
            .RequireAuthorization()
            .WithTags("Channels")
            .WithName("CreateChannel")
            .WithSummary("Creates a text or voice channel. Requires ManageChannels.")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        var channels = endpoints.MapGroup("/channels/{channelId:guid}").WithTags("Channels").RequireAuthorization();

        channels.MapGet("/", ChannelEndpoints.GetAsync)
            .WithName("GetChannel")
            .WithSummary("Returns a channel the caller can view.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        channels.MapPatch("/", ChannelEndpoints.UpdateAsync)
            .WithName("UpdateChannel")
            .WithSummary("Renames, re-topics or moves a channel. Position is its zero-based place among channels of its type; the others shift. Requires ManageChannels.")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        channels.MapDelete("/", ChannelEndpoints.DeleteAsync)
            .WithName("DeleteChannel")
            .WithSummary("Deletes a channel. Requires ManageChannels.")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    public static Task MigrateChannelsDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default) =>
        services.MigrateModuleAsync<ChannelsDbContext>(cancellationToken);
}

internal sealed class ChannelsModuleApi(ChannelsDbContext db, ICommunitiesModule communities, TimeProvider timeProvider) : IChannelsModule
{
    public const string DefaultTextChannelName = "general";
    public const string DefaultVoiceChannelName = "General";
    public Task<bool> CanReadChannelAsync(Guid channelId, Guid userId, CancellationToken cancellationToken = default) =>
        HasAsync(channelId, userId, requiredType: null, CommunityPermissions.ViewChannels, cancellationToken);

    public Task<bool> CanWriteMessagesAsync(Guid channelId, Guid userId, CancellationToken cancellationToken = default) =>
        HasAsync(channelId, userId, ChannelType.Text, CommunityPermissions.ViewChannels | CommunityPermissions.SendMessages, cancellationToken);

    public Task<bool> CanManageMessagesAsync(Guid channelId, Guid userId, CancellationToken cancellationToken = default) =>
        HasAsync(channelId, userId, ChannelType.Text, CommunityPermissions.ViewChannels | CommunityPermissions.ManageMessages, cancellationToken);

    public Task<bool> CanJoinVoiceAsync(Guid channelId, Guid userId, CancellationToken cancellationToken = default) =>
        HasAsync(channelId, userId, ChannelType.Voice, CommunityPermissions.ViewChannels | CommunityPermissions.ConnectVoice, cancellationToken);

    public async Task<IReadOnlyList<Guid>?> ListReadableTextChannelIdsAsync(Guid communityId, Guid userId, CancellationToken cancellationToken = default)
    {
        // Permissions are community-wide today; per-channel overrides would filter here.
        var permissions = await communities.GetPermissionsAsync(communityId, userId, cancellationToken);
        if (!permissions.HasFlag(CommunityPermissions.ViewChannels))
        {
            return null;
        }

        return await db.Channels.AsNoTracking()
            .Where(c => c.CommunityId == communityId && c.Type == ChannelType.Text && c.DeletedAt == null)
            .OrderBy(c => c.Position).ThenBy(c => c.CreatedAt)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> ListVoiceChannelIdsAsync(Guid communityId, CancellationToken cancellationToken = default) =>
        await db.Channels.AsNoTracking()
            .Where(c => c.CommunityId == communityId && c.Type == ChannelType.Voice && c.DeletedAt == null)
            .OrderBy(c => c.Position).ThenBy(c => c.CreatedAt)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

    public async Task CreateDefaultChannelsAsync(Guid communityId, Guid createdBy, CancellationToken cancellationToken = default)
    {
        if (await db.Channels.AnyAsync(c => c.CommunityId == communityId, cancellationToken))
        {
            return;
        }

        var now = timeProvider.GetDatabaseUtcNow();
        db.Channels.AddRange(
            Channel.Create(communityId, DefaultTextChannelName, ChannelType.Text, topic: null, position: 0, createdBy, now),
            Channel.Create(communityId, DefaultVoiceChannelName, ChannelType.Voice, topic: null, position: 0, createdBy, now));
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> HasAsync(Guid channelId, Guid userId, ChannelType? requiredType, CommunityPermissions required, CancellationToken cancellationToken)
    {
        var channel = await db.Channels.AsNoTracking()
            .Where(c => c.Id == channelId && c.DeletedAt == null)
            .Select(c => new { c.CommunityId, c.Type })
            .SingleOrDefaultAsync(cancellationToken);

        if (channel is null || (requiredType is { } type && channel.Type != type))
        {
            return false;
        }

        var permissions = await communities.GetPermissionsAsync(channel.CommunityId, userId, cancellationToken);
        return (permissions & required) == required;
    }
}
