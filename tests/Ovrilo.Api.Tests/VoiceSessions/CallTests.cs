using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;

using Ovrilo.Api.Tests.Communities;
using Ovrilo.Api.Tests.Identity;
using Ovrilo.Modules.Messaging.Features;
using Ovrilo.Modules.Social.Features;
using Ovrilo.Modules.VoiceSessions.Features;
using Ovrilo.Modules.VoiceSessions.LiveKit;

namespace Ovrilo.Api.Tests.VoiceSessions;

/// <summary>One-to-one calls in direct conversations, with a fake SFU directory and a short ring time.</summary>
[Collection(SharedApiFixture.Name)]
public sealed class CallTests(OvriloApiFactory factory)
{
    private readonly Rooms _rooms = new();

    private WebApplicationFactory<Program> App => factory.WithWebHostBuilder(builder =>
    {
        builder.UseSetting("VoiceSessions:Calls:RingSeconds", "1");
        builder.ConfigureTestServices(services => services.AddSingleton<IVoiceRoomDirectory>(_rooms));
    });

    [Fact]
    public async Task CallingRingsTheFriendAndAnsweringConnectsBoth()
    {
        var app = App;
        var (ada, bob, dm) = await FriendsWithConversationAsync(app);
        await using var adaEvents = await ListenAsync(app, ada);
        await using var bobEvents = await ListenAsync(app, bob);

        var grant = await CallAsync(ada, dm, "");
        Assert.Equal($"dm_{dm:N}", RoomOf(grant));
        var ringing = await bobEvents.NextAsync<CallRingingEvent>(CallEvents.CallRinging);
        Assert.Equal((dm, ada.Id), (ringing.ConversationId, ringing.Caller.Id));
        Assert.Equal(ada.Id, (await StateAsync(bob, dm)).RingingFrom);

        _rooms.Set(dm, ada.Id);
        await CallAsync(bob, dm, "/join");
        Assert.Equal(dm, (await adaEvents.NextAsync<CallAcceptedEvent>(CallEvents.CallAccepted)).ConversationId);
        var state = await StateAsync(ada, dm);
        Assert.Null(state.RingingFrom);
        Assert.Equal([ada.Id], state.ParticipantIds);

        ada.Dispose();
        bob.Dispose();
    }

    [Fact]
    public async Task DecliningCancellingAndNotAnsweringEndTheRing()
    {
        var app = App;
        var (ada, bob, dm) = await FriendsWithConversationAsync(app);
        await using var adaEvents = await ListenAsync(app, ada);

        await CallAsync(ada, dm, "");
        Assert.Equal(HttpStatusCode.NoContent, (await bob.PostAsync($"/dms/{dm}/call/decline", new { })).StatusCode);
        Assert.Equal(CallEndReason.Declined, (await adaEvents.NextAsync<CallEndedEvent>(CallEvents.CallEnded)).Reason);
        // Idempotent.
        Assert.Equal(HttpStatusCode.NoContent, (await bob.PostAsync($"/dms/{dm}/call/decline", new { })).StatusCode);

        await CallAsync(ada, dm, "");
        await ada.PostAsync($"/dms/{dm}/call/decline", new { });
        Assert.Equal(CallEndReason.Cancelled, (await adaEvents.NextAsync<CallEndedEvent>(CallEvents.CallEnded)).Reason);

        await CallAsync(ada, dm, "");
        Assert.Equal(CallEndReason.Missed, (await adaEvents.NextAsync<CallEndedEvent>(CallEvents.CallEnded)).Reason);
        Assert.Null((await StateAsync(ada, dm)).RingingFrom);

        ada.Dispose();
        bob.Dispose();
    }

    [Fact]
    public async Task CallingSomeoneWhoIsCallingYouAnswersThem()
    {
        var app = App;
        var (ada, bob, dm) = await FriendsWithConversationAsync(app);
        await using var adaEvents = await ListenAsync(app, ada);

        await CallAsync(ada, dm, "");
        await CallAsync(bob, dm, "");
        Assert.Equal(dm, (await adaEvents.NextAsync<CallAcceptedEvent>(CallEvents.CallAccepted)).ConversationId);
        Assert.Null((await StateAsync(ada, dm)).RingingFrom);

        ada.Dispose();
        bob.Dispose();
    }

    [Fact]
    public async Task OnlyFriendsInTheConversationCanCall()
    {
        var app = App;
        var (ada, bob, dm) = await FriendsWithConversationAsync(app);
        using var eve = await TestUser.RegisterAsync(app);

        Assert.Equal(HttpStatusCode.NotFound, (await eve.PostAsync($"/dms/{dm}/call", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await eve.GetAsync($"/dms/{dm}/call")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ada.PostAsync($"/dms/{Guid.NewGuid()}/call", new { })).StatusCode);

        await ada.Client.DeleteAsync(new Uri($"/friends/{bob.Id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden, (await ada.PostAsync($"/dms/{dm}/call", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.PostAsync($"/dms/{dm}/call/join", new { })).StatusCode);

        ada.Dispose();
        bob.Dispose();
    }

    private static async Task<(TestUser Ada, TestUser Bob, Guid Conversation)> FriendsWithConversationAsync(WebApplicationFactory<Program> app)
    {
        var ada = await TestUser.RegisterAsync(app);
        var bob = await TestUser.RegisterAsync(app);
        await ada.PostAsync("/friends/requests", new SendFriendRequestRequest(bob.Auth.User.Username));
        await bob.PostAsync("/friends/requests", new SendFriendRequestRequest(ada.Auth.User.Username));
        var opened = await ada.PostAsync("/dms", new OpenDirectMessageRequest(bob.Id));
        var conversation = (await opened.Content.ReadFromJsonAsync<DirectConversationResponse>(TestJson.Options))!;
        return (ada, bob, conversation.Id);
    }

    private static async Task<VoiceJoinResponse> CallAsync(TestUser user, Guid conversation, string action)
    {
        var response = await user.PostAsync($"/dms/{conversation}/call{action}", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<VoiceJoinResponse>(TestJson.Options))!;
    }

    private static async Task<CallStateResponse> StateAsync(TestUser user, Guid conversation) =>
        (await (await user.GetAsync($"/dms/{conversation}/call")).Content.ReadFromJsonAsync<CallStateResponse>(TestJson.Options))!;

    private static string RoomOf(VoiceJoinResponse grant)
    {
        using var video = JsonDocument.Parse(new JsonWebTokenHandler().ReadJsonWebToken(grant.Token).GetClaim("video").Value);
        return video.RootElement.GetProperty("room").GetString()!;
    }

    private static async Task<Listener> ListenAsync(WebApplicationFactory<Program> app, TestUser user)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(app.Server.BaseAddress, "hubs/realtime"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(user.Auth.AccessToken);
            })
            .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false)))
            .Build();
        var listener = new Listener(connection);
        await connection.StartAsync();
        return listener;
    }

    private sealed class Listener : IAsyncDisposable
    {
        private readonly HubConnection _connection;
        private readonly Channel<(string Name, object Payload)> _events = Channel.CreateUnbounded<(string, object)>();

        public Listener(HubConnection connection)
        {
            _connection = connection;
            connection.On<CallRingingEvent>(CallEvents.CallRinging, e => _events.Writer.TryWrite((CallEvents.CallRinging, e)));
            connection.On<CallAcceptedEvent>(CallEvents.CallAccepted, e => _events.Writer.TryWrite((CallEvents.CallAccepted, e)));
            connection.On<CallEndedEvent>(CallEvents.CallEnded, e => _events.Writer.TryWrite((CallEvents.CallEnded, e)));
        }

        /// <summary>The next event with this name (others are skipped).</summary>
        public async Task<T> NextAsync<T>(string name)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                var (eventName, payload) = await _events.Reader.ReadAsync(timeout.Token);
                if (eventName == name)
                {
                    return (T)payload;
                }
            }
        }

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }

    private sealed class Rooms : IVoiceRoomDirectory
    {
        private readonly Dictionary<string, IReadOnlyList<RoomParticipant>> _rooms = [];

        public void Set(Guid conversation, params Guid[] users) =>
            _rooms[$"dm_{conversation:N}"] = [.. users.Select(u => new RoomParticipant(u.ToString(), false))];

        public Task<IReadOnlyDictionary<string, IReadOnlyList<RoomParticipant>>> GetParticipantsAsync(
            IReadOnlyCollection<string> rooms,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<RoomParticipant>>>(
                rooms.Where(_rooms.ContainsKey).ToDictionary(r => r, r => _rooms[r]));
    }
}
