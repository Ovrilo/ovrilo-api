using System.Security.Claims;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

using Ovrilo.BuildingBlocks;
using Ovrilo.Modules.Channels.Contracts;
using Ovrilo.Modules.Identity.Contracts;
using Ovrilo.Modules.VoiceSessions.LiveKit;

namespace Ovrilo.Modules.VoiceSessions.Features;

/// <summary>
/// Short-lived credentials for connecting the voice engine to the channel's SFU room.
/// The token is a secret: clients hand it to the voice engine and never log or persist it.
/// </summary>
public sealed record VoiceJoinResponse(string ServerUrl, string Token, DateTimeOffset ExpiresAt);

internal static class JoinVoice
{
    public static async Task<Results<Ok<VoiceJoinResponse>, ProblemHttpResult>> HandleAsync(
        Guid channelId,
        ClaimsPrincipal principal,
        IChannelsModule channels,
        IIdentityModule identity,
        LiveKitTokenIssuer issuer,
        CancellationToken cancellationToken)
    {
        var userId = principal.GetRequiredUserId();
        if (!await channels.CanReadChannelAsync(channelId, userId, cancellationToken))
        {
            // Same response as Channels for invisible channels, so existence is not revealed.
            return ApiProblems.Create(StatusCodes.Status404NotFound, "channels.not_found", "Channel not found.");
        }

        var user = await identity.GetPublicUserAsync(userId, cancellationToken);
        if (user is null || !await channels.CanJoinVoiceAsync(channelId, userId, cancellationToken))
        {
            return ApiProblems.Create(StatusCodes.Status403Forbidden, "voice.permission_denied", "You cannot join voice in this channel.");
        }

        var grant = issuer.IssueVoiceGrant(channelId, userId, user.DisplayName);
        return TypedResults.Ok(new VoiceJoinResponse(grant.ServerUrl, grant.Token, grant.ExpiresAt));
    }
}
