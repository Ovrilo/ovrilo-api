using System.Text;

using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Ovrilo.Modules.VoiceSessions.LiveKit;

internal sealed record LiveKitGrant(string ServerUrl, string Token, string RoomName, DateTimeOffset ExpiresAt);

/// <summary>
/// Issues LiveKit access tokens (HS256 JWT signed with the API secret; https://docs.livekit.io/home/get-started/authentication/).
/// Grants are limited to joining one room and publishing the microphone.
/// </summary>
internal sealed class LiveKitTokenIssuer(IOptions<LiveKitOptions> options, TimeProvider timeProvider)
{
    private static readonly string[] PublishSources = ["microphone"];

    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };

    /// <summary>Opaque room name; it is never an authorization credential.</summary>
    public static string RoomName(Guid channelId) => $"channel_{channelId:N}";

    public LiveKitGrant IssueVoiceGrant(Guid channelId, Guid userId, string displayName)
    {
        var liveKit = options.Value;
        var now = timeProvider.GetUtcNow();
        var expiresAt = now.AddMinutes(liveKit.TokenLifetimeMinutes);
        var room = RoomName(channelId);

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = liveKit.ApiKey,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = userId.ToString(),
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"),
                ["name"] = displayName,
                ["video"] = new Dictionary<string, object>
                {
                    ["room"] = room,
                    ["roomJoin"] = true,
                    ["canPublish"] = true,
                    ["canSubscribe"] = true,
                    ["canPublishData"] = false,
                    ["canPublishSources"] = PublishSources,
                },
            },
            SigningCredentials = new SigningCredentials(CreateSigningKey(liveKit), SecurityAlgorithms.HmacSha256),
        });

        return new LiveKitGrant(liveKit.ServerUrl, token, room, expiresAt);
    }

    public static SymmetricSecurityKey CreateSigningKey(LiveKitOptions liveKit) => new(Encoding.UTF8.GetBytes(liveKit.ApiSecret));
}
