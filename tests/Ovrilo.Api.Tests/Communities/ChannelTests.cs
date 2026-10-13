using System.Net;
using System.Net.Http.Json;

using Microsoft.Extensions.DependencyInjection;

using Ovrilo.Api.Tests.Identity;
using Ovrilo.Modules.Channels.Contracts;
using Ovrilo.Modules.Channels.Features;

namespace Ovrilo.Api.Tests.Communities;

[Collection(SharedApiFixture.Name)]
public sealed class ChannelTests(OvriloApiFactory factory)
{
    [Fact]
    public async Task OwnerCreatesChannelsAndMembersSeeThem()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        using var friend = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();
        await friend.AcceptAsync((await owner.CreateInvitationAsync(community.Id)).Code);

        var general = await owner.CreateChannelAsync(community.Id, "general", "text");
        var clips = await owner.CreateChannelAsync(community.Id, "clips", "text");
        var lobby = await owner.CreateChannelAsync(community.Id, "Lobby", "voice");

        var channels = await friend.Client.GetFromJsonAsync<List<ChannelResponse>>(new Uri($"/communities/{community.Id}/channels", UriKind.Relative), TestJson.Options);
        Assert.Equal([general.Id, clips.Id, lobby.Id], channels!.Select(c => c.Id));
        Assert.Equal([0, 1, 0], channels!.Select(c => c.Position));
        Assert.Equal(ChannelType.Voice, lobby.Type);

        var single = await friend.GetAsync($"/channels/{general.Id}");
        Assert.Equal(HttpStatusCode.OK, single.StatusCode);
    }

    [Fact]
    public async Task NonMembersCannotListOrReadChannels()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        using var stranger = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();
        var channel = await owner.CreateChannelAsync(community.Id, "general", "text");

        var list = await stranger.GetAsync($"/communities/{community.Id}/channels");
        var get = await stranger.GetAsync($"/channels/{channel.Id}");
        var create = await stranger.PostAsync($"/communities/{community.Id}/channels", new { name = "x", type = "text" });

        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal("channels.not_found", await get.ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.NotFound, create.StatusCode);
    }

    [Fact]
    public async Task MembersCannotManageChannels()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        using var friend = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();
        await friend.AcceptAsync((await owner.CreateInvitationAsync(community.Id)).Code);
        var channel = await owner.CreateChannelAsync(community.Id, "general", "text");

        var create = await friend.PostAsync($"/communities/{community.Id}/channels", new { name = "mine", type = "text" });
        var update = await friend.Client.PatchAsJsonAsync(new Uri($"/channels/{channel.Id}", UriKind.Relative), new UpdateChannelRequest("renamed", null, null));
        var delete = await friend.Client.DeleteAsync(new Uri($"/channels/{channel.Id}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal("channels.permission_denied", await create.ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }

    [Fact]
    public async Task ChannelNamesAreUniquePerTypeIgnoringCase()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();
        await owner.CreateChannelAsync(community.Id, "General", "text");

        var duplicate = await owner.PostAsync($"/communities/{community.Id}/channels", new { name = "general", type = "text" });
        var otherType = await owner.PostAsync($"/communities/{community.Id}/channels", new { name = "general", type = "voice" });

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("channels.name_taken", await duplicate.ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.Created, otherType.StatusCode);
    }

    [Fact]
    public async Task OwnerUpdatesAndDeletesChannel()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();
        var channel = await owner.CreateChannelAsync(community.Id, "general", "text");

        var update = await owner.Client.PatchAsJsonAsync(new Uri($"/channels/{channel.Id}", UriKind.Relative), new UpdateChannelRequest("chat", "Daily chatter", 3));
        var updated = (await update.Content.ReadFromJsonAsync<ChannelResponse>(TestJson.Options))!;
        var delete = await owner.Client.DeleteAsync(new Uri($"/channels/{channel.Id}", UriKind.Relative));
        var afterDelete = await owner.GetAsync($"/channels/{channel.Id}");
        var recreate = await owner.PostAsync($"/communities/{community.Id}/channels", new { name = "chat", type = "text" });

        Assert.Equal(("chat", "Daily chatter", 3), (updated.Name, updated.Topic, updated.Position));
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, afterDelete.StatusCode);
        Assert.Equal(HttpStatusCode.Created, recreate.StatusCode);
    }

    [Theory]
    [InlineData("", "text")]
    [InlineData("valid", "video")]
    public async Task InvalidChannelInputIsRejected(string name, string type)
    {
        using var owner = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();

        var response = await owner.PostAsync($"/communities/{community.Id}/channels", new { name, type });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ChannelsContractReflectsMembershipAndChannelType()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        using var stranger = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();
        var text = await owner.CreateChannelAsync(community.Id, "general", "text");
        var voice = await owner.CreateChannelAsync(community.Id, "Lobby", "voice");

        await using var scope = factory.Services.CreateAsyncScope();
        var channels = scope.ServiceProvider.GetRequiredService<IChannelsModule>();

        Assert.True(await channels.CanReadChannelAsync(text.Id, owner.Id));
        Assert.True(await channels.CanWriteMessagesAsync(text.Id, owner.Id));
        Assert.False(await channels.CanWriteMessagesAsync(voice.Id, owner.Id));
        Assert.True(await channels.CanJoinVoiceAsync(voice.Id, owner.Id));
        Assert.False(await channels.CanJoinVoiceAsync(text.Id, owner.Id));
        Assert.False(await channels.CanReadChannelAsync(text.Id, stranger.Id));
        Assert.False(await channels.CanJoinVoiceAsync(voice.Id, stranger.Id));
    }
}
