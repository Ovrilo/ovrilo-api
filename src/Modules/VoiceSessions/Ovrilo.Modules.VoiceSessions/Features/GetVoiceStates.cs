using System.Security.Claims;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

using Ovrilo.BuildingBlocks;
using Ovrilo.Modules.Channels.Contracts;
using Ovrilo.Modules.Communities.Contracts;
using Ovrilo.Modules.Identity.Contracts;
using Ovrilo.Modules.VoiceSessions.LiveKit;

namespace Ovrilo.Modules.VoiceSessions.Features;

/// <summary>Who is in one voice channel right now. Channels nobody is in are omitted.</summary>
public sealed record VoiceChannelStateResponse(Guid ChannelId, IReadOnlyList<VoiceParticipantResponse> Participants);

/// <summary>
/// A user in a voice channel. <c>Muted</c> is their own published microphone mute; deafen is not
/// signalled to the server yet.
/// </summary>
public sealed record VoiceParticipantResponse(Guid UserId, string DisplayName, bool Muted);

internal static class GetVoiceStates
{
    public static async Task<Results<Ok<List<VoiceChannelStateResponse>>, ProblemHttpResult>> HandleAsync(
        Guid communityId,
        ClaimsPrincipal principal,
        ICommunitiesModule communities,
        IChannelsModule channels,
        IIdentityModule identity,
        IVoiceRoomDirectory rooms,
        CancellationToken cancellationToken)
    {
        var permissions = await communities.GetPermissionsAsync(communityId, principal.GetRequiredUserId(), cancellationToken);
        if (!permissions.HasFlag(CommunityPermissions.ViewChannels))
        {
            return ApiProblems.Create(StatusCodes.Status404NotFound, "communities.not_found", "Community not found.");
        }

        var voiceChannels = await channels.ListVoiceChannelIdsAsync(communityId, cancellationToken);
        if (voiceChannels.Count == 0)
        {
            return TypedResults.Ok(new List<VoiceChannelStateResponse>());
        }

        IReadOnlyDictionary<string, IReadOnlyList<RoomParticipant>> occupied;
        try
        {
            occupied = await rooms.GetParticipantsAsync(
                voiceChannels.Select(LiveKitTokenIssuer.RoomName).ToList(),
                cancellationToken);
        }
        catch (VoiceRoomDirectoryUnavailableException)
        {
            return ApiProblems.Create(StatusCodes.Status503ServiceUnavailable, "voice.sfu_unavailable", "Voice server is unavailable.");
        }

        // Identities are user IDs from our own grants; anything else is ignored.
        var userIds = occupied.Values.SelectMany(p => p)
            .Select(p => Guid.TryParse(p.Identity, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        var users = userIds.Count == 0
            ? new Dictionary<Guid, PublicUser>()
            : await identity.GetPublicUsersAsync(userIds, cancellationToken);

        var result = new List<VoiceChannelStateResponse>();
        foreach (var channelId in voiceChannels)
        {
            if (!occupied.TryGetValue(LiveKitTokenIssuer.RoomName(channelId), out var participants))
            {
                continue;
            }

            var visible = participants
                .Select(p => Guid.TryParse(p.Identity, out var id) && users.TryGetValue(id, out var user)
                    ? new VoiceParticipantResponse(id, user.DisplayName, p.MicrophoneMuted)
                    : null)
                .OfType<VoiceParticipantResponse>()
                .OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (visible.Count > 0)
            {
                result.Add(new VoiceChannelStateResponse(channelId, visible));
            }
        }

        return TypedResults.Ok(result);
    }
}
