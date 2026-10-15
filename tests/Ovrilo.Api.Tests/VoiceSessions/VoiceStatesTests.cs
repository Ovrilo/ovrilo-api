using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Ovrilo.Api.Tests.Communities;
using Ovrilo.Api.Tests.Identity;
using Ovrilo.Modules.VoiceSessions.Features;
using Ovrilo.Modules.VoiceSessions.LiveKit;

namespace Ovrilo.Api.Tests.VoiceSessions;

/// <summary>
/// Occupancy endpoint against the real database with a fake SFU directory (rooms are filled per test).
/// The LiveKit directory itself is exercised manually against a local SFU (see README).
/// </summary>
[Collection(SharedApiFixture.Name)]
public sealed class VoiceStatesTests(OvriloApiFactory factory)
{
    private readonly FakeRoomDirectory _rooms = new();

    private WebApplicationFactory<Program> App => factory.WithWebHostBuilder(builder =>
        builder.ConfigureTestServices(services => services.AddSingleton<IVoiceRoomDirectory>(_rooms)));

    [Fact]
    public async Task MembersSeeWhoIsInEachVoiceChannelWithNamesFromIdentity()
    {
        var app = App;
        using var owner = await TestUser.RegisterAsync(app);
        using var friend = await TestUser.RegisterAsync(app);
        var community = await owner.CreateCommunityAsync();
        await friend.AcceptAsync((await owner.CreateInvitationAsync(community.Id)).Code);
        var general = await owner.DefaultChannelAsync(community.Id, "voice");
        var lobby = await owner.CreateChannelAsync(community.Id, "Lobby", "voice");
        var empty = await owner.CreateChannelAsync(community.Id, "Empty", "voice");

        _rooms.Set(lobby.Id, new RoomParticipant(owner.Id.ToString(), MicrophoneMuted: true), new RoomParticipant("not-a-user-id", false));

        var states = await GetStatesAsync(friend, community.Id);

        var state = Assert.Single(states);
        Assert.Equal(lobby.Id, state.ChannelId);
        var participant = Assert.Single(state.Participants);
        Assert.Equal(owner.Id, participant.UserId);
        Assert.Equal(owner.Auth.User.DisplayName, participant.DisplayName);
        Assert.True(participant.Muted);
        // Only the community's voice channels are looked up, never text channels (the default text channel exists).
        Assert.Equal(
            new[] { $"channel_{general.Id:N}", $"channel_{lobby.Id:N}", $"channel_{empty.Id:N}" }.Order(StringComparer.Ordinal),
            _rooms.Queried.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task NonMembersCannotSeeVoiceStates()
    {
        var app = App;
        using var owner = await TestUser.RegisterAsync(app);
        using var stranger = await TestUser.RegisterAsync(app);
        var community = await owner.CreateCommunityAsync();
        var lobby = await owner.CreateChannelAsync(community.Id, "Lobby", "voice");
        _rooms.Set(lobby.Id, new RoomParticipant(owner.Id.ToString(), false));

        var response = await stranger.GetAsync($"/communities/{community.Id}/voice-states");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_rooms.Queried);
    }

    [Fact]
    public async Task CommunitiesWithoutVoiceChannelsDoNotQueryTheSfu()
    {
        var app = App;
        using var owner = await TestUser.RegisterAsync(app);
        var community = await owner.CreateCommunityAsync();
        await owner.DeleteChannelAsync((await owner.DefaultChannelAsync(community.Id, "voice")).Id);

        Assert.Empty(await GetStatesAsync(owner, community.Id));
        Assert.Empty(_rooms.Queried);
    }

    [Fact]
    public async Task SfuOutageIsReportedAsServiceUnavailable()
    {
        var app = App;
        using var owner = await TestUser.RegisterAsync(app);
        var community = await owner.CreateCommunityAsync();
        await owner.CreateChannelAsync(community.Id, "Lobby", "voice");
        _rooms.Fail = true;

        var response = await owner.GetAsync($"/communities/{community.Id}/voice-states");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("voice.sfu_unavailable", await response.ProblemCodeAsync());
    }

    private static async Task<List<VoiceChannelStateResponse>> GetStatesAsync(TestUser user, Guid communityId)
    {
        var response = await user.GetAsync($"/communities/{communityId}/voice-states");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<VoiceChannelStateResponse>>(TestJson.Options))!;
    }

    private sealed class FakeRoomDirectory : IVoiceRoomDirectory
    {
        private readonly Dictionary<string, IReadOnlyList<RoomParticipant>> _rooms = [];

        public List<string> Queried { get; } = [];

        public bool Fail { get; set; }

        public void Set(Guid channelId, params RoomParticipant[] participants) =>
            _rooms[$"channel_{channelId:N}"] = participants;

        public Task<IReadOnlyDictionary<string, IReadOnlyList<RoomParticipant>>> GetParticipantsAsync(
            IReadOnlyCollection<string> rooms,
            CancellationToken cancellationToken = default)
        {
            Queried.AddRange(rooms);
            if (Fail)
            {
                throw new VoiceRoomDirectoryUnavailableException("SFU down");
            }

            IReadOnlyDictionary<string, IReadOnlyList<RoomParticipant>> result =
                rooms.Where(_rooms.ContainsKey).ToDictionary(r => r, r => _rooms[r]);
            return Task.FromResult(result);
        }
    }
}
