using System.Security.Claims;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

using Ovrilo.BuildingBlocks;
using Ovrilo.Modules.Channels.Contracts;
using Ovrilo.Modules.Identity.Contracts;
using Ovrilo.Modules.VoiceSessions.LiveKit;

namespace Ovrilo.Modules.VoiceSessions.Features;

/// <summary>
/// Credentials for the webview's watch-only connection to a voice channel's SFU room (identity <c>{userId}#viewer</c>,
/// hidden, subscribe-only). The token is a secret like the join token.
/// </summary>
public sealed record VoiceWatchResponse(string ServerUrl, string Token, DateTimeOffset ExpiresAt);

/// <summary>Screen-share video is watched in the webview; audio and voice stay in the engine (ADR-0017).</summary>
internal static class WatchScreenShares
{
    public static async Task<Results<Ok<VoiceWatchResponse>, ProblemHttpResult>> HandleAsync(
        Guid channelId,
        ClaimsPrincipal principal,
        IChannelsModule channels,
        IIdentityModule identity,
        LiveKitTokenIssuer issuer,
        IOptions<ScreenSharingOptions> screenSharing,
        CancellationToken cancellationToken)
    {
        var userId = principal.GetRequiredUserId();
        if (!await channels.CanReadChannelAsync(channelId, userId, cancellationToken))
        {
            return ApiProblems.Create(StatusCodes.Status404NotFound, "channels.not_found", "Channel not found.");
        }

        if (!screenSharing.Value.Enabled)
        {
            return ApiProblems.Create(StatusCodes.Status403Forbidden, "voice.screen_share_disabled", "Screen sharing is not enabled on this server.");
        }

        // Watching needs the same rights as joining the channel's voice.
        var user = await identity.GetPublicUserAsync(userId, cancellationToken);
        if (user is null || !await channels.CanJoinVoiceAsync(channelId, userId, cancellationToken))
        {
            return ApiProblems.Create(StatusCodes.Status403Forbidden, "voice.permission_denied", "You cannot join voice in this channel.");
        }

        var grant = issuer.IssueViewerGrant(channelId, userId, user.DisplayName);
        return TypedResults.Ok(new VoiceWatchResponse(grant.ServerUrl, grant.Token, grant.ExpiresAt));
    }
}
