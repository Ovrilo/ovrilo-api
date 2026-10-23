using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

using Ovrilo.Api.Realtime;
using Ovrilo.Api.Tests.Communities;
using Ovrilo.Api.Tests.Identity;
using Ovrilo.Modules.Identity.Contracts;
using Ovrilo.Modules.Identity.Features;
using Ovrilo.Modules.Messaging.Features;

namespace Ovrilo.Api.Tests.Messaging;

/// <summary>Online status over the realtime hub, and the send cooldown clients show after 429.</summary>
[Collection(SharedApiFixture.Name)]
public sealed class PresenceTests(OvriloApiFactory factory)
{
    private async Task<HubConnection> ConnectAsync(TestUser user)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "hubs/realtime"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(user.Auth.AccessToken);
            })
            .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false)))
            .Build();
        await connection.StartAsync();
        return connection;
    }

    [Fact]
    public async Task MembersSeeEachOtherComeOnlineAndGoOffline()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        using var member = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();
        await member.AcceptAsync((await owner.CreateInvitationAsync(community.Id)).Code);

        await using var watcher = await ConnectAsync(owner);
        var events = Channel.CreateUnbounded<PresenceChangedEvent>();
        watcher.On<PresenceChangedEvent>(PresenceTracker.PresenceChanged, e => events.Writer.TryWrite(e));

        var snapshot = await watcher.InvokeAsync<CommunityPresenceResponse?>("SubscribeCommunity", community.Id);
        Assert.Equal([owner.Auth.User.Id], snapshot!.OnlineUserIds);

        var memberConnection = await ConnectAsync(member);
        Assert.Equal(new PresenceChangedEvent(member.Auth.User.Id, true, Availability.Online), await NextAsync(events));

        // A second connection (another device) is not a new arrival.
        var secondDevice = await ConnectAsync(member);
        await secondDevice.DisposeAsync();
        Assert.False(await HasEventWithinAsync(events, TimeSpan.FromMilliseconds(500)));

        await memberConnection.DisposeAsync();
        Assert.Equal(new PresenceChangedEvent(member.Auth.User.Id, false), await NextAsync(events));
    }

    [Fact]
    public async Task ChosenStatusAndIdleShowToOthersAndInvisibleLooksOffline()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        using var member = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();
        await member.AcceptAsync((await owner.CreateInvitationAsync(community.Id)).Code);

        await using var watcher = await ConnectAsync(owner);
        var events = Channel.CreateUnbounded<PresenceChangedEvent>();
        watcher.On<PresenceChangedEvent>(PresenceTracker.PresenceChanged, e => events.Writer.TryWrite(e));
        await watcher.InvokeAsync<CommunityPresenceResponse?>("SubscribeCommunity", community.Id);

        var memberId = member.Auth.User.Id;
        await using var device = await ConnectAsync(member);
        Assert.Equal(new PresenceChangedEvent(memberId, true, Availability.Online), await NextAsync(events));

        // Do not disturb is stored on the account and shown right away.
        Assert.Equal(HttpStatusCode.NoContent, (await SetStatusAsync(member, Availability.DoNotDisturb)).StatusCode);
        Assert.Equal(new PresenceChangedEvent(memberId, true, Availability.DoNotDisturb), await NextAsync(events));
        var me = await member.Client.GetFromJsonAsync<CurrentUserResponse>(new Uri("/users/me", UriKind.Relative), TestJson.Options);
        Assert.Equal(Availability.DoNotDisturb, me!.Availability);
        var snapshot = await watcher.InvokeAsync<CommunityPresenceResponse?>("SubscribeCommunity", community.Id);
        Assert.Contains(new UserPresence(memberId, Availability.DoNotDisturb), snapshot!.Statuses!);

        // Online again; an idle computer shows as away until input returns.
        await SetStatusAsync(member, Availability.Online);
        Assert.Equal(new PresenceChangedEvent(memberId, true, Availability.Online), await NextAsync(events));
        await device.InvokeAsync("SetIdle", true);
        Assert.Equal(new PresenceChangedEvent(memberId, true, Availability.Away), await NextAsync(events));
        await device.InvokeAsync("SetIdle", false);
        Assert.Equal(new PresenceChangedEvent(memberId, true, Availability.Online), await NextAsync(events));

        // Invisible looks offline, also in snapshots, and a new device keeps the choice.
        await SetStatusAsync(member, Availability.Invisible);
        Assert.Equal(new PresenceChangedEvent(memberId, false), await NextAsync(events));
        snapshot = await watcher.InvokeAsync<CommunityPresenceResponse?>("SubscribeCommunity", community.Id);
        Assert.DoesNotContain(memberId, snapshot!.OnlineUserIds);
        await using var secondDevice = await ConnectAsync(member);
        Assert.False(await HasEventWithinAsync(events, TimeSpan.FromMilliseconds(500)));
    }

    private static Task<HttpResponseMessage> SetStatusAsync(TestUser user, Availability availability) =>
        user.Client.PutAsJsonAsync(new Uri("/users/me/status", UriKind.Relative), new SetStatusRequest(availability), TestJson.Options);

    [Fact]
    public async Task OutsidersCannotWatchACommunity()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        using var stranger = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();

        await using var connection = await ConnectAsync(stranger);
        var events = Channel.CreateUnbounded<PresenceChangedEvent>();
        connection.On<PresenceChangedEvent>(PresenceTracker.PresenceChanged, e => events.Writer.TryWrite(e));

        Assert.Null(await connection.InvokeAsync<CommunityPresenceResponse?>("SubscribeCommunity", community.Id));
        await using var ownerConnection = await ConnectAsync(owner);
        Assert.False(await HasEventWithinAsync(events, TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task RateLimitedSendsSayWhenToRetry()
    {
        using var limited = factory.WithWebHostBuilder(b => b.UseSetting("Messaging:RateLimit:PermitLimit", "2"));
        using var owner = await TestUser.RegisterAsync(limited);
        var community = await owner.CreateCommunityAsync();
        var general = await owner.DefaultChannelAsync(community.Id, "text");

        HttpResponseMessage? last = null;
        for (var i = 0; i < 3; i++)
        {
            last?.Dispose();
            last = await owner.PostAsync($"/channels/{general.Id}/messages", new SendMessageRequest(Guid.NewGuid(), $"spam {i}", null));
        }

        using (last)
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
            var retryAfter = Assert.Single(last.Headers.GetValues("Retry-After"));
            Assert.InRange(int.Parse(retryAfter, CultureInfo.InvariantCulture), 1, 5);
        }
    }

    private static async Task<PresenceChangedEvent> NextAsync(Channel<PresenceChangedEvent> events)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await events.Reader.ReadAsync(timeout.Token);
    }

    private static async Task<bool> HasEventWithinAsync(Channel<PresenceChangedEvent> events, TimeSpan wait)
    {
        using var timeout = new CancellationTokenSource(wait);
        try
        {
            await events.Reader.ReadAsync(timeout.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
