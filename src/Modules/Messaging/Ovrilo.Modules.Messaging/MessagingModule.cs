using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Ovrilo.BuildingBlocks;
using Ovrilo.BuildingBlocks.Persistence;
using Ovrilo.Modules.Identity.Contracts;
using Ovrilo.Modules.Messaging.Contracts;
using Ovrilo.Modules.Messaging.Features;
using Ovrilo.Modules.Messaging.Persistence;

namespace Ovrilo.Modules.Messaging;

/// <summary>
/// Composition entry points for the Messaging module (docs/domain/messaging.md, ADR-0007).
/// Durable messages over REST; realtime fan-out through IRealtimePublisher.
/// </summary>
public static class MessagingModule
{
    /// <summary>Per-user anti-spam limit on sending messages.</summary>
    public const string SendRateLimitPolicy = "messaging-send";

    private const string RateLimitSection = "Messaging:RateLimit";

    public static IServiceCollection AddMessagingModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<MessagingDbContext>(MessagingDbContext.Schema);
        services.AddScoped<ConversationAccess>();
        services.AddScoped<IDirectConversations, DirectConversationsApi>();
        services.AddScoped<IAccountDeletionParticipant, MessagingAccountDeletion>();
        services.AddRateLimiter(options => options.AddPolicy(SendRateLimitPolicy, context =>
        {
            var section = context.RequestServices.GetRequiredService<IConfiguration>().GetSection(RateLimitSection);
            var user = context.User.FindFirst(IdentityClaims.UserId)?.Value ?? "anonymous";
            return RateLimitPartition.GetFixedWindowLimiter(user, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = section.GetValue("PermitLimit", 5),
                Window = TimeSpan.FromSeconds(section.GetValue("WindowSeconds", 5)),
                QueueLimit = 0,
            });
        }));
        return services;
    }

    public static IEndpointRouteBuilder MapMessagingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var channelMessages = endpoints.MapGroup("/channels/{channelId:guid}/messages").WithTags("Messages").RequireAuthorization();

        channelMessages.MapPost("/", MessageEndpoints.SendAsync)
            .RequireRateLimiting(SendRateLimitPolicy)
            .WithName("SendMessage")
            .WithSummary("Posts a message in a text channel. Resending the same clientMessageId returns the original message (200).")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        channelMessages.MapGet("/", MessageEndpoints.ListAsync)
            .WithName("ListMessages")
            .WithSummary("One page of channel history in ascending order: newest page, or older ('before') / newer ('after') than a message ID.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapPut("/channels/{channelId:guid}/read-state", ReadStateEndpoints.MarkReadAsync)
            .RequireAuthorization()
            .WithTags("Messages")
            .WithName("MarkChannelRead")
            .WithSummary("Marks a text channel read up to a message. Positions only move forward; the stored position is returned.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapGet("/communities/{communityId:guid}/unread", ReadStateEndpoints.ListUnreadAsync)
            .RequireAuthorization()
            .WithTags("Messages")
            .WithName("ListCommunityUnread")
            .WithSummary("Read position, newest message and unread count (capped at 100) of each readable text channel. History from before the caller joined counts as read.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        var dms = endpoints.MapGroup("/dms").WithTags("Direct messages").RequireAuthorization();

        dms.MapPost("/", DirectMessageEndpoints.OpenAsync)
            .WithName("OpenDirectMessage")
            .WithSummary("Opens the direct conversation with a friend (201), or returns the existing one (200). Use its id as the channel id for messages and read state.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        dms.MapGet("/", DirectMessageEndpoints.ListAsync)
            .WithName("ListDirectMessages")
            .WithSummary("Your direct conversations with unread counts, most recently active first.");

        var groups = endpoints.MapGroup("/groups").WithTags("Group conversations").RequireAuthorization();

        groups.MapPost("/", GroupEndpoints.CreateAsync)
            .WithName("CreateGroup")
            .WithSummary("Starts a group conversation with 1 to 9 of your friends. Use its id as the channel id for messages, read state and calls.")
            .ProducesProblem(StatusCodes.Status403Forbidden);

        groups.MapGet("/", GroupEndpoints.ListAsync)
            .WithName("ListGroups")
            .WithSummary("Your group conversations with members and unread counts, most recently active first.");

        groups.MapPatch("/{groupId:guid}", GroupEndpoints.RenameAsync)
            .WithName("RenameGroup")
            .WithSummary("Renames a group (any member). An empty name clears it.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        groups.MapPost("/{groupId:guid}/members", GroupEndpoints.AddMemberAsync)
            .WithName("AddGroupMember")
            .WithSummary("Adds one of your friends to a group you are in (up to 10 people).")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        groups.MapDelete("/{groupId:guid}/members/{userId:guid}", GroupEndpoints.RemoveMemberAsync)
            .WithName("RemoveGroupMember")
            .WithSummary("Leaves a group (your own id), or removes someone as its owner. The oldest member becomes owner when the owner leaves.")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        var messages = endpoints.MapGroup("/messages/{messageId:long}").WithTags("Messages").RequireAuthorization();

        messages.MapPatch("/", MessageEndpoints.EditAsync)
            .WithName("EditMessage")
            .WithSummary("Edits your own message.")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        messages.MapDelete("/", MessageEndpoints.DeleteAsync)
            .WithName("DeleteMessage")
            .WithSummary("Deletes a message (yours, or anyone's with ManageMessages). Leaves a tombstone.")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return endpoints;
    }

    public static Task MigrateMessagingDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default) =>
        services.MigrateModuleAsync<MessagingDbContext>(cancellationToken);
}
