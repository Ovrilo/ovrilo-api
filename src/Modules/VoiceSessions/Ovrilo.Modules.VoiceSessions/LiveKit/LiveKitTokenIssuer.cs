using System.Text;

using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Ovrilo.Modules.VoiceSessions.LiveKit;

internal sealed record LiveKitGrant(string ServerUrl, string Token, string RoomName, DateTimeOffset ExpiresAt);

/// <summary>
/// Issues LiveKit access tokens (HS256 JWT signed with the API secret; https://docs.livekit.io/home/get-started/authentication/).
/// Grants are limited to one room: the voice grant publishes the microphone (and, when allowed, the screen share and its
/// audio); the viewer grant only subscribes.
/// </summary>
internal sealed class LiveKitTokenIssuer(IOptions<LiveKitOptions> options, TimeProvider timeProvider)
{
    private static readonly string[] VoiceSources = ["microphone"];
    private static readonly string[] VoiceAndScreenSources = ["microphone", "screen_share", "screen_share_audio"];

    /// <summary>Suffix of the identity of a client's second, watch-only connection (ADR-0017).</summary>
    public const string ViewerIdentitySuffix = "#viewer";

    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };

    /// <summary>Opaque room name; it is never an authorization credential.</summary>
    public static string RoomName(Guid channelId) => $"channel_{channelId:N}";

    /// <summary>The voice engine's connection: identity is the user ID.</summary>
    public LiveKitGrant IssueVoiceGrant(Guid channelId, Guid userId, string displayName, bool canShareScreen = false) =>
        Issue(channelId, userId.ToString(), displayName, new Dictionary<string, object>
        {
            ["canPublish"] = true,
            ["canSubscribe"] = true,
            ["canPublishData"] = false,
            ["canPublishSources"] = canShareScreen ? VoiceAndScreenSources : VoiceSources,
        });

    /// <summary>
    /// The webview's watch-only connection for screen-share video: its own identity (one identity per connection),
    /// hidden from other participants and unable to publish anything.
    /// </summary>
    public LiveKitGrant IssueViewerGrant(Guid channelId, Guid userId, string displayName) =>
        Issue(channelId, userId + ViewerIdentitySuffix, displayName, new Dictionary<string, object>
        {
            ["canPublish"] = false,
            ["canSubscribe"] = true,
            ["canPublishData"] = false,
            ["hidden"] = true,
        });

    private LiveKitGrant Issue(Guid channelId, string identity, string displayName, Dictionary<string, object> permissions)
    {
        var liveKit = options.Value;
        var now = timeProvider.GetUtcNow();
        var expiresAt = now.AddMinutes(liveKit.TokenLifetimeMinutes);
        var room = RoomName(channelId);
        var video = new Dictionary<string, object>(permissions)
        {
            ["room"] = room,
            ["roomJoin"] = true,
        };

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = liveKit.ApiKey,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = identity,
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"),
                ["name"] = displayName,
                ["video"] = video,
            },
            SigningCredentials = new SigningCredentials(CreateSigningKey(liveKit), SecurityAlgorithms.HmacSha256),
        });

        return new LiveKitGrant(liveKit.ServerUrl, token, room, expiresAt);
    }

    public static SymmetricSecurityKey CreateSigningKey(LiveKitOptions liveKit) => new(Encoding.UTF8.GetBytes(liveKit.ApiSecret));
}
