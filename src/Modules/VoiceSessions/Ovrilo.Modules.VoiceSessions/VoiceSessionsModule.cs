using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using Ovrilo.Modules.VoiceSessions.Features;
using Ovrilo.Modules.VoiceSessions.LiveKit;

namespace Ovrilo.Modules.VoiceSessions;

/// <summary>
/// Composition entry points for the Voice Sessions module (docs/domain/voice-sessions.md).
/// Phase 02 issues authorized LiveKit grants only; session/participant tracking comes later.
/// </summary>
public static class VoiceSessionsModule
{
    public static IServiceCollection AddVoiceSessionsModule(this IServiceCollection services)
    {
        services.AddOptions<LiveKitOptions>()
            .BindConfiguration(LiveKitOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<LiveKitTokenIssuer>();
        return services;
    }

    public static IEndpointRouteBuilder MapVoiceSessionsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/channels/{channelId:guid}/voice/join", JoinVoice.HandleAsync)
            .RequireAuthorization()
            .WithTags("Voice")
            .WithName("JoinVoiceChannel")
            .WithSummary("Issues a short-lived SFU grant for a voice channel the caller may join. Requires ConnectVoice.")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }
}
