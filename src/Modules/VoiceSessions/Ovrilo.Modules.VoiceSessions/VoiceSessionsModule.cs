using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using Ovrilo.Modules.VoiceSessions.Features;
using Ovrilo.Modules.VoiceSessions.LiveKit;

namespace Ovrilo.Modules.VoiceSessions;

/// <summary>
/// Composition entry points for the Voice Sessions module (docs/domain/voice-sessions.md).
/// Issues authorized LiveKit grants and reports channel occupancy from the SFU (no local session
/// persistence yet; realtime push arrives with SignalR in Phase 04).
/// </summary>
public static class VoiceSessionsModule
{
    public static IServiceCollection AddVoiceSessionsModule(this IServiceCollection services)
    {
        services.AddOptions<LiveKitOptions>()
            .BindConfiguration(LiveKitOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<ScreenSharingOptions>().BindConfiguration(ScreenSharingOptions.SectionName);
        services.AddSingleton<LiveKitTokenIssuer>();
        services.AddMemoryCache();
        services.AddHttpClient<IVoiceRoomDirectory, LiveKitRoomDirectory>(client => client.Timeout = TimeSpan.FromSeconds(3));
        return services;
    }

    public static IEndpointRouteBuilder MapVoiceSessionsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/channels/{channelId:guid}/voice/join", JoinVoice.HandleAsync)
            .RequireAuthorization()
            .WithTags("Voice")
            .WithName("JoinVoiceChannel")
            .WithSummary("Issues a short-lived SFU grant for a voice channel the caller may join. Requires ConnectVoice; screen-share sources are added with ShareScreen while the server has screen sharing on.")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapPost("/channels/{channelId:guid}/voice/watch", WatchScreenShares.HandleAsync)
            .RequireAuthorization()
            .WithTags("Voice")
            .WithName("WatchVoiceChannelScreenShares")
            .WithSummary("Issues a short-lived, hidden, subscribe-only SFU grant for watching screen shares in a voice channel. Same rights as joining; 403 voice.screen_share_disabled while the server has screen sharing off.")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapGet("/communities/{communityId:guid}/voice-states", GetVoiceStates.HandleAsync)
            .RequireAuthorization()
            .WithTags("Voice")
            .WithName("GetVoiceStates")
            .WithSummary("Lists who is in each voice channel of a community the caller can view. Channels nobody is in are omitted.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return endpoints;
    }
}
