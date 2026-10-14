using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Channels;

using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

using Ovrilo.Api.Tests.Communities;
using Ovrilo.Api.Tests.Identity;
using Ovrilo.Modules.Communities.Features;
using Ovrilo.Modules.Identity.Features;
using Ovrilo.Modules.Media;
using Ovrilo.Modules.Media.Contracts;
using Ovrilo.Modules.Media.Features;

namespace Ovrilo.Api.Tests.Media;

/// <summary>Image uploads straight to storage (RustFS container), verification, avatars and community icons.</summary>
[Collection(SharedApiFixture.Name)]
public sealed class MediaTests(OvriloApiFactory factory)
{
    private static Task<HttpResponseMessage> PostJsonAsync<T>(TestUser user, string path, T body) =>
        user.Client.PostAsJsonAsync(new Uri(path, UriKind.Relative), body, TestJson.Options);

    private static Task<HttpResponseMessage> PutJsonAsync<T>(TestUser user, string path, T body) =>
        user.Client.PutAsJsonAsync(new Uri(path, UriKind.Relative), body, TestJson.Options);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(TestJson.Options))!;

    private static async Task<UploadResponse> StartUploadAsync(TestUser user, MediaPurpose purpose, string contentType, byte[] bytes, Guid? communityId = null)
    {
        var response = await PostJsonAsync(user, "/media/uploads", new CreateUploadRequest(purpose, contentType, bytes.Length, "picture", communityId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<UploadResponse>(response);
    }

    /// <summary>What the desktop does: PUT the bytes to the presigned URL, outside the API.</summary>
    private static async Task PutToStorageAsync(UploadResponse upload, byte[] bytes)
    {
        using var http = new HttpClient();
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(upload.ContentType);
        var response = await http.PutAsync(upload.UploadUrl, content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Task<HttpResponseMessage> CompleteAsync(TestUser user, Guid assetId) =>
        user.Client.PostAsync(new Uri($"/media/uploads/{assetId}/complete", UriKind.Relative), null);

    private static async Task<MediaAssetResponse> UploadAsync(TestUser user, MediaPurpose purpose, string contentType, byte[] bytes, Guid? communityId = null)
    {
        var upload = await StartUploadAsync(user, purpose, contentType, bytes, communityId);
        await PutToStorageAsync(upload, bytes);
        var response = await CompleteAsync(user, upload.AssetId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<MediaAssetResponse>(response);
    }

    private static async Task<IReadOnlyList<DownloadUrl>> DownloadUrlsAsync(TestUser user, params Guid[] assetIds)
    {
        var response = await PostJsonAsync(user, "/media/download-urls", new DownloadUrlsRequest(assetIds));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadAsync<DownloadUrlsResponse>(response)).Urls;
    }

    private Task<int> RunJanitorAsync() => factory.Services.GetRequiredService<MediaJanitor>().RunOnceAsync();

    [Fact]
    public async Task AnimatedGifAvatarIsUploadedVerifiedAndVisibleToOthers()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        using var member = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();
        await member.AcceptAsync((await owner.CreateInvitationAsync(community.Id)).Code);

        var asset = await UploadAsync(owner, MediaPurpose.Avatar, "image/gif", TestImages.AnimatedGif);
        Assert.Equal(new MediaAssetResponse(asset.Id, MediaPurpose.Avatar, "image/gif", TestImages.AnimatedGif.Length, 7, 6), asset);

        var set = await PutJsonAsync(owner, "/users/me/avatar", new SetAvatarRequest(asset.Id));
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal(asset.Id, (await ReadAsync<CurrentUserResponse>(set)).AvatarAssetId);
        Assert.Equal(asset.Id, (await ReadAsync<CurrentUserResponse>(await owner.GetAsync("/users/me"))).AvatarAssetId);

        var members = await ReadAsync<List<MemberResponse>>(await member.GetAsync($"/communities/{community.Id}/members"));
        Assert.Equal(asset.Id, members.Single(m => m.UserId == owner.Id).AvatarAssetId);
        Assert.Null(members.Single(m => m.UserId == member.Id).AvatarAssetId);

        var url = Assert.Single(await DownloadUrlsAsync(member, asset.Id));
        Assert.Equal("image/gif", url.ContentType);
        using var http = new HttpClient();
        var download = await http.GetAsync(url.Url);
        Assert.Equal("image/gif", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal(TestImages.AnimatedGif, await download.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task ReplacingTheAvatarDeletesThePreviousImage()
    {
        using var user = await TestUser.RegisterAsync(factory);
        var first = await UploadAsync(user, MediaPurpose.Avatar, "image/png", TestImages.Png);
        var second = await UploadAsync(user, MediaPurpose.Avatar, "image/webp", TestImages.AnimatedWebP);

        await PutJsonAsync(user, "/users/me/avatar", new SetAvatarRequest(first.Id));
        await PutJsonAsync(user, "/users/me/avatar", new SetAvatarRequest(second.Id));

        Assert.Equal([second.Id], (await DownloadUrlsAsync(user, first.Id, second.Id)).Select(u => u.AssetId));

        var removed = await user.Client.DeleteAsync(new Uri("/users/me/avatar", UriKind.Relative));
        Assert.Null((await ReadAsync<CurrentUserResponse>(removed)).AvatarAssetId);
        Assert.Empty(await DownloadUrlsAsync(user, first.Id, second.Id));

        // Released images can no longer be set again.
        var reuse = await PutJsonAsync(user, "/users/me/avatar", new SetAvatarRequest(first.Id));
        Assert.Equal(HttpStatusCode.BadRequest, reuse.StatusCode);
    }

    [Fact]
    public async Task JanitorDeletesReleasedAndRejectedObjects()
    {
        using var user = await TestUser.RegisterAsync(factory);
        var released = await StartUploadAsync(user, MediaPurpose.Avatar, "image/png", TestImages.Png);
        await PutToStorageAsync(released, TestImages.Png);
        await CompleteAsync(user, released.AssetId);
        await PutJsonAsync(user, "/users/me/avatar", new SetAvatarRequest(released.AssetId));
        await user.Client.DeleteAsync(new Uri("/users/me/avatar", UriKind.Relative));

        var disguised = await StartUploadAsync(user, MediaPurpose.Avatar, "image/png", "<html></html>"u8.ToArray());
        await PutToStorageAsync(disguised, "<html></html>"u8.ToArray());
        await CompleteAsync(user, disguised.AssetId);

        Assert.True(await ExistsInStorageAsync(released) && await ExistsInStorageAsync(disguised));
        Assert.True(await RunJanitorAsync() >= 2);
        Assert.False(await ExistsInStorageAsync(released));
        Assert.False(await ExistsInStorageAsync(disguised));
    }

    private async Task<bool> ExistsInStorageAsync(UploadResponse upload)
    {
        // Path-style presigned URL: /<bucket>/<key>.
        var key = Uri.UnescapeDataString(upload.UploadUrl.AbsolutePath).Split('/', 3)[2];
        using var s3 = factory.Storage.CreateClient();
        try
        {
            await s3.GetObjectMetadataAsync(StorageContainer.Bucket, key);
            return true;
        }
        catch (Amazon.S3.AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    [Theory]
    [InlineData("image/png", "<!doctype html><script>alert(1)</script>")]
    [InlineData("image/gif", "<svg xmlns=\"http://www.w3.org/2000/svg\"/>")]
    public async Task FilesThatAreNotTheDeclaredImageAreRejected(string declared, string body)
    {
        using var user = await TestUser.RegisterAsync(factory);
        var bytes = Encoding.UTF8.GetBytes(body);
        var upload = await StartUploadAsync(user, MediaPurpose.Avatar, declared, bytes);
        await PutToStorageAsync(upload, bytes);

        var complete = await CompleteAsync(user, upload.AssetId);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, complete.StatusCode);
        Assert.Equal("media.invalid_image", await complete.ProblemCodeAsync());
        Assert.Empty(await DownloadUrlsAsync(user, upload.AssetId));
        Assert.Equal(HttpStatusCode.BadRequest, (await PutJsonAsync(user, "/users/me/avatar", new SetAvatarRequest(upload.AssetId))).StatusCode);
    }

    [Fact]
    public async Task AGifDeclaredAsPngIsRejected()
    {
        using var user = await TestUser.RegisterAsync(factory);
        var upload = await StartUploadAsync(user, MediaPurpose.Avatar, "image/png", TestImages.AnimatedGif);
        await PutToStorageAsync(upload, TestImages.AnimatedGif);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CompleteAsync(user, upload.AssetId)).StatusCode);
    }

    [Fact]
    public async Task OversizedDimensionsAreRejected()
    {
        using var user = await TestUser.RegisterAsync(factory);
        var upload = await StartUploadAsync(user, MediaPurpose.Avatar, "image/png", TestImages.WidePng);
        await PutToStorageAsync(upload, TestImages.WidePng);

        var complete = await CompleteAsync(user, upload.AssetId);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, complete.StatusCode);
    }

    [Fact]
    public async Task UploadRequestsAreValidated()
    {
        using var user = await TestUser.RegisterAsync(factory);

        var svg = await PostJsonAsync(user, "/media/uploads", new CreateUploadRequest(MediaPurpose.Avatar, "image/svg+xml", 100, null, null));
        var huge = await PostJsonAsync(user, "/media/uploads", new CreateUploadRequest(MediaPurpose.Avatar, "image/gif", 50L * 1024 * 1024, null, null));
        var iconWithoutCommunity = await PostJsonAsync(user, "/media/uploads", new CreateUploadRequest(MediaPurpose.CommunityIcon, "image/png", 100, null, null));

        Assert.Equal(HttpStatusCode.BadRequest, svg.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, huge.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, iconWithoutCommunity.StatusCode);
    }

    [Fact]
    public async Task CompletingBeforeUploadingAsksToUploadFirst()
    {
        using var user = await TestUser.RegisterAsync(factory);
        var upload = await StartUploadAsync(user, MediaPurpose.Avatar, "image/png", TestImages.Png);

        var early = await CompleteAsync(user, upload.AssetId);

        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Equal("media.upload_missing", await early.ProblemCodeAsync());
        await PutToStorageAsync(upload, TestImages.Png);
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(user, upload.AssetId)).StatusCode);
        // Retrying completion is harmless.
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(user, upload.AssetId)).StatusCode);
    }

    [Fact]
    public async Task OthersCannotCompleteOrUseSomeoneElsesUpload()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        using var other = await TestUser.RegisterAsync(factory);
        var upload = await StartUploadAsync(owner, MediaPurpose.Avatar, "image/png", TestImages.Png);
        await PutToStorageAsync(upload, TestImages.Png);

        Assert.Equal(HttpStatusCode.NotFound, (await CompleteAsync(other, upload.AssetId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(owner, upload.AssetId)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutJsonAsync(other, "/users/me/avatar", new SetAvatarRequest(upload.AssetId))).StatusCode);
    }

    [Fact]
    public async Task PendingUploadsPerUserAreLimited()
    {
        using var user = await TestUser.RegisterAsync(factory);
        for (var i = 0; i < 5; i++)
        {
            await StartUploadAsync(user, MediaPurpose.Avatar, "image/png", TestImages.Png);
        }

        var sixth = await PostJsonAsync(user, "/media/uploads", new CreateUploadRequest(MediaPurpose.Avatar, "image/png", 100, null, null));

        Assert.Equal(HttpStatusCode.TooManyRequests, sixth.StatusCode);
        Assert.Equal("media.too_many_pending", await sixth.ProblemCodeAsync());
    }

    [Fact]
    public async Task CommunityIconsNeedManageCommunityAndAreVisibleOnlyToMembers()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        using var member = await TestUser.RegisterAsync(factory);
        using var stranger = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();
        await member.AcceptAsync((await owner.CreateInvitationAsync(community.Id)).Code);

        var byMember = await PostJsonAsync(member, "/media/uploads", new CreateUploadRequest(MediaPurpose.CommunityIcon, "image/png", 100, null, community.Id));
        var byStranger = await PostJsonAsync(stranger, "/media/uploads", new CreateUploadRequest(MediaPurpose.CommunityIcon, "image/png", 100, null, community.Id));
        Assert.Equal(HttpStatusCode.Forbidden, byMember.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, byStranger.StatusCode);

        var icon = await UploadAsync(owner, MediaPurpose.CommunityIcon, "image/jpeg", TestImages.Jpeg, community.Id);
        var set = await PutJsonAsync(owner, $"/communities/{community.Id}/icon", new SetCommunityIconRequest(icon.Id));
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal(icon.Id, (await ReadAsync<CommunityResponse>(await member.GetAsync($"/communities/{community.Id}"))).IconAssetId);

        Assert.Single(await DownloadUrlsAsync(member, icon.Id));
        Assert.Empty(await DownloadUrlsAsync(stranger, icon.Id));
        Assert.Equal(HttpStatusCode.Forbidden, (await PutJsonAsync(member, $"/communities/{community.Id}/icon", new SetCommunityIconRequest(icon.Id))).StatusCode);

        // An icon uploaded for one community cannot be used for another.
        var other = await owner.CreateCommunityAsync("Other");
        Assert.Equal(HttpStatusCode.BadRequest, (await PutJsonAsync(owner, $"/communities/{other.Id}/icon", new SetCommunityIconRequest(icon.Id))).StatusCode);

        var removed = await owner.Client.DeleteAsync(new Uri($"/communities/{community.Id}/icon", UriKind.Relative));
        Assert.Null((await ReadAsync<CommunityResponse>(removed)).IconAssetId);
    }

    [Fact]
    public async Task MembersAreToldWhenSomeonesAvatarChanges()
    {
        using var owner = await TestUser.RegisterAsync(factory);
        using var member = await TestUser.RegisterAsync(factory);
        var community = await owner.CreateCommunityAsync();
        await member.AcceptAsync((await owner.CreateInvitationAsync(community.Id)).Code);

        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "hubs/realtime"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(member.Auth.AccessToken);
            })
            .Build();
        var events = Channel.CreateUnbounded<UserUpdatedEvent>();
        connection.On<UserUpdatedEvent>(IdentityEvents.UserUpdated, e => events.Writer.TryWrite(e));
        await connection.StartAsync();
        await connection.InvokeAsync<object>("SubscribeCommunity", community.Id);

        var asset = await UploadAsync(owner, MediaPurpose.Avatar, "image/gif", TestImages.AnimatedGif);
        await PutJsonAsync(owner, "/users/me/avatar", new SetAvatarRequest(asset.Id));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var updated = await events.Reader.ReadAsync(timeout.Token);
        Assert.Equal(new UserUpdatedEvent(owner.Id, owner.Auth.User.DisplayName, asset.Id), updated);
    }
}
