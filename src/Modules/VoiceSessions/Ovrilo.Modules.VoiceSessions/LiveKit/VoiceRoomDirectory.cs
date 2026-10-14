using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Ovrilo.Modules.VoiceSessions.LiveKit;

/// <summary>A participant currently in an SFU room. <c>Identity</c> is the user ID issued in the grant.</summary>
public sealed record RoomParticipant(string Identity, bool MicrophoneMuted);

/// <summary>Who is in which SFU room. The LiveKit implementation is replaced in tests.</summary>
public interface IVoiceRoomDirectory
{
    /// <summary>Participants per room for the given rooms; empty or unknown rooms are omitted.</summary>
    /// <exception cref="VoiceRoomDirectoryUnavailableException">The SFU could not be queried.</exception>
    Task<IReadOnlyDictionary<string, IReadOnlyList<RoomParticipant>>> GetParticipantsAsync(
        IReadOnlyCollection<string> rooms,
        CancellationToken cancellationToken = default);
}

public sealed class VoiceRoomDirectoryUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Queries LiveKit's RoomService (Twirp/JSON) with short-lived service tokens: one ListRooms call
/// finds occupied rooms, then ListParticipants runs only for those. Results are cached briefly so
/// many clients refreshing the same community cost one SFU query per room per few seconds.
/// </summary>
internal sealed class LiveKitRoomDirectory(
    HttpClient http,
    IOptions<LiveKitOptions> options,
    IMemoryCache cache,
    TimeProvider timeProvider) : IVoiceRoomDirectory
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(3);
    private static readonly IReadOnlyList<RoomParticipant> Empty = [];

    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<RoomParticipant>>> GetParticipantsAsync(
        IReadOnlyCollection<string> rooms,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, IReadOnlyList<RoomParticipant>>();
        var uncached = new List<string>();
        foreach (var room in rooms)
        {
            if (cache.TryGetValue(CacheKey(room), out IReadOnlyList<RoomParticipant>? participants))
            {
                Add(result, room, participants!);
            }
            else
            {
                uncached.Add(room);
            }
        }

        if (uncached.Count == 0)
        {
            return result;
        }

        var listed = await CallAsync<ListRoomsResponse>(
            "ListRooms",
            new ListRoomsRequest(uncached),
            new Dictionary<string, object> { ["roomList"] = true },
            cancellationToken);
        var occupied = listed.Rooms?.Where(r => r.NumParticipants > 0).Select(r => r.Name).ToHashSet() ?? [];

        foreach (var room in uncached)
        {
            var participants = Empty;
            if (occupied.Contains(room))
            {
                var response = await CallAsync<ListParticipantsResponse>(
                    "ListParticipants",
                    new ListParticipantsRequest(room),
                    new Dictionary<string, object> { ["roomAdmin"] = true, ["room"] = room },
                    cancellationToken);
                participants = response.Participants?
                    .Where(p => p.State is "JOINED" or "ACTIVE" && !string.IsNullOrEmpty(p.Identity))
                    .Select(p => new RoomParticipant(
                        p.Identity!,
                        p.Tracks?.Any(t => t.Source == "MICROPHONE" && t.Muted) ?? false))
                    .ToList() ?? Empty;
            }

            cache.Set(CacheKey(room), participants, CacheFor);
            Add(result, room, participants);
        }

        return result;
    }

    private static void Add(Dictionary<string, IReadOnlyList<RoomParticipant>> result, string room, IReadOnlyList<RoomParticipant> participants)
    {
        if (participants.Count > 0)
        {
            result[room] = participants;
        }
    }

    private static string CacheKey(string room) => "livekit-room:" + room;

    private async Task<TResponse> CallAsync<TResponse>(
        string method,
        object request,
        Dictionary<string, object> videoGrant,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiBase(), $"twirp/livekit.RoomService/{method}"))
        {
            Content = JsonContent.Create(request),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ServiceToken(videoGrant));
        try
        {
            using var response = await http.SendAsync(message, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken)
                ?? throw new VoiceRoomDirectoryUnavailableException($"Empty {method} response.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new VoiceRoomDirectoryUnavailableException($"LiveKit {method} failed.", ex);
        }
    }

    /// <summary>HTTP base URL of the SFU: explicit <c>ApiUrl</c>, or the client URL with ws→http.</summary>
    private Uri ApiBase()
    {
        var liveKit = options.Value;
        if (!string.IsNullOrEmpty(liveKit.ApiUrl))
        {
            return new Uri(liveKit.ApiUrl.TrimEnd('/') + "/");
        }

        var builder = new UriBuilder(liveKit.ServerUrl)
        {
            Scheme = liveKit.ServerUrl.StartsWith("wss", StringComparison.OrdinalIgnoreCase) ? "https" : "http",
        };
        builder.Path = builder.Path.TrimEnd('/') + "/";
        return builder.Uri;
    }

    private string ServiceToken(Dictionary<string, object> videoGrant)
    {
        var liveKit = options.Value;
        var now = timeProvider.GetUtcNow();
        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = liveKit.ApiKey,
            NotBefore = now.UtcDateTime,
            Expires = now.AddMinutes(1).UtcDateTime,
            Claims = new Dictionary<string, object> { ["video"] = videoGrant },
            SigningCredentials = new SigningCredentials(LiveKitTokenIssuer.CreateSigningKey(liveKit), SecurityAlgorithms.HmacSha256),
        });
    }

    private sealed record ListRoomsRequest([property: JsonPropertyName("names")] IReadOnlyList<string> Names);

    private sealed record ListParticipantsRequest([property: JsonPropertyName("room")] string Room);

    private sealed record ListRoomsResponse([property: JsonPropertyName("rooms")] List<RoomInfo>? Rooms);

    private sealed record RoomInfo(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("num_participants")] int NumParticipants);

    private sealed record ListParticipantsResponse([property: JsonPropertyName("participants")] List<ParticipantInfo>? Participants);

    private sealed record ParticipantInfo(
        [property: JsonPropertyName("identity")] string? Identity,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("tracks")] List<TrackInfo>? Tracks);

    private sealed record TrackInfo(
        [property: JsonPropertyName("source")] string? Source,
        [property: JsonPropertyName("muted")] bool Muted);
}
