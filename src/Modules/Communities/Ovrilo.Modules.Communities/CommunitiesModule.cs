using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Ovrilo.BuildingBlocks.Persistence;
using Ovrilo.Modules.Communities.Contracts;
using Ovrilo.Modules.Communities.Domain;
using Ovrilo.Modules.Communities.Features;
using Ovrilo.Modules.Communities.Persistence;
using Ovrilo.Modules.Identity.Contracts;

namespace Ovrilo.Modules.Communities;

/// <summary>Composition entry points for the Communities module. Other modules use <see cref="ICommunitiesModule"/>.</summary>
public static class CommunitiesModule
{
    /// <summary>Per-user limit on invitation acceptance to slow down code guessing.</summary>
    public const string InvitationRateLimitPolicy = "communities-invitations";

    private const string RateLimitSection = "Communities:RateLimit";

    public static IServiceCollection AddCommunitiesModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<CommunitiesDbContext>(CommunitiesDbContext.Schema);
        services.AddScoped<ICommunitiesModule, CommunitiesModuleApi>();

        services.AddRateLimiter(options => options.AddPolicy(InvitationRateLimitPolicy, context =>
        {
            var section = context.RequestServices.GetRequiredService<IConfiguration>().GetSection(RateLimitSection);
            return RateLimitPartition.GetFixedWindowLimiter(
                context.User.GetUserId()?.ToString() ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = section.GetValue("PermitLimit", 20),
                    Window = TimeSpan.FromSeconds(section.GetValue("WindowSeconds", 60)),
                    QueueLimit = 0,
                });
        }));

        return services;
    }

    public static IEndpointRouteBuilder MapCommunitiesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var communities = endpoints.MapGroup("/communities").WithTags("Communities").RequireAuthorization();

        communities.MapPost("/", CommunityEndpoints.CreateAsync)
            .WithName("CreateCommunity")
            .WithSummary("Creates a private community owned by the caller, with a text channel `general` and a voice channel `General`.");

        communities.MapGet("/", CommunityEndpoints.ListMineAsync)
            .WithName("ListMyCommunities")
            .WithSummary("Lists communities the caller is an active member of.");

        communities.MapGet("/{communityId:guid}", CommunityEndpoints.GetAsync)
            .WithName("GetCommunity")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Returns a community the caller is a member of.");

        communities.MapPatch("/{communityId:guid}", CommunityEndpoints.UpdateAsync)
            .WithName("UpdateCommunity")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Renames the community or changes its description. Needs ManageCommunity.");

        communities.MapDelete("/{communityId:guid}", CommunityEndpoints.DeleteAsync)
            .WithName("DeleteCommunity")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Deletes the community for everyone. Owner only; cannot be undone.");

        communities.MapGet("/{communityId:guid}/members", CommunityEndpoints.ListMembersAsync)
            .WithName("ListCommunityMembers")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Lists active members.");

        communities.MapPut("/{communityId:guid}/icon", CommunityIconEndpoints.SetAsync)
            .WithName("SetCommunityIcon")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Sets the community icon to a completed upload made for it. Needs ManageCommunity.");

        communities.MapDelete("/{communityId:guid}/icon", CommunityIconEndpoints.RemoveAsync)
            .WithName("RemoveCommunityIcon")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Removes the community icon. Needs ManageCommunity.");

        communities.MapPost("/{communityId:guid}/leave", CommunityEndpoints.LeaveAsync)
            .WithName("LeaveCommunity")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithSummary("Leaves the community. The owner cannot leave.");

        communities.MapPost("/{communityId:guid}/invitations", InvitationEndpoints.CreateAsync)
            .WithName("CreateInvitation")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Creates an expiring invitation. The code is returned only once.");

        communities.MapDelete("/{communityId:guid}/invitations/{invitationId:guid}", InvitationEndpoints.RevokeAsync)
            .WithName("RevokeInvitation")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Revokes an invitation.");

        endpoints.MapPost("/invitations/accept", InvitationEndpoints.AcceptAsync)
            .RequireAuthorization()
            .RequireRateLimiting(InvitationRateLimitPolicy)
            .WithTags("Communities")
            .WithName("AcceptInvitation")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Joins a community using an invitation code.")
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return endpoints;
    }

    public static Task MigrateCommunitiesDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default) =>
        services.MigrateModuleAsync<CommunitiesDbContext>(cancellationToken);
}

internal sealed class CommunitiesModuleApi(CommunitiesDbContext db) : ICommunitiesModule
{
    public async Task<CommunityPermissions> GetPermissionsAsync(Guid communityId, Guid userId, CancellationToken cancellationToken = default)
    {
        var role = await db.Members.AsNoTracking()
            .Where(m => m.CommunityId == communityId && m.UserId == userId && m.State == MembershipState.Active)
            .Join(db.Communities.Where(c => c.DeletedAt == null), m => m.CommunityId, c => c.Id, (m, _) => (CommunityRole?)m.Role)
            .SingleOrDefaultAsync(cancellationToken);

        return role is { } r ? RolePermissions.For(r) : CommunityPermissions.None;
    }

    public async Task<IReadOnlyList<Guid>> ListCommunityIdsAsync(Guid userId, CancellationToken cancellationToken = default) =>
        await db.Members.AsNoTracking()
            .Where(m => m.UserId == userId && m.State == MembershipState.Active)
            .Join(db.Communities.Where(c => c.DeletedAt == null), m => m.CommunityId, c => c.Id, (m, _) => m.CommunityId)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> ListMemberIdsAsync(Guid communityId, CancellationToken cancellationToken = default) =>
        await db.Members.AsNoTracking()
            .Where(m => m.CommunityId == communityId && m.State == MembershipState.Active)
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken);

    public async Task<DateTimeOffset?> GetJoinedAtAsync(Guid communityId, Guid userId, CancellationToken cancellationToken = default) =>
        await db.Members.AsNoTracking()
            .Where(m => m.CommunityId == communityId && m.UserId == userId && m.State == MembershipState.Active)
            .Join(db.Communities.Where(c => c.DeletedAt == null), m => m.CommunityId, c => c.Id, (m, _) => (DateTimeOffset?)m.JoinedAt)
            .SingleOrDefaultAsync(cancellationToken);
}
