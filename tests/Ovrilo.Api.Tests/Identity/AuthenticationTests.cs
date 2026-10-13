using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;

using Ovrilo.Modules.Identity.Features;

namespace Ovrilo.Api.Tests.Identity;

[Collection(SharedApiFixture.Name)]
public sealed class AuthenticationTests(OvriloApiFactory factory)
{
    [Fact]
    public async Task RegisterSignsInAndReturnsCurrentUser()
    {
        using var client = factory.CreateClient();
        var username = IdentityTestClient.UniqueUsername("Reg");

        var response = await client.RegisterAsync(username, $"  {username}@Example.COM ");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        Assert.Equal(username.ToLowerInvariant(), auth.User.Username);
        Assert.Equal($"{username}@example.com".ToLowerInvariant(), auth.User.Email);
        Assert.False(auth.User.EmailVerified);
        Assert.True(auth.SessionExpiresAt > auth.AccessTokenExpiresAt);

        var me = await client.SendAuthorizedAsync(HttpMethod.Get, "/users/me", auth.AccessToken);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(auth.User.Id, (await me.Content.ReadFromJsonAsync<CurrentUserResponse>())!.Id);
    }

    [Fact]
    public async Task TwoUsersCanRegister()
    {
        using var client = factory.CreateClient();

        var first = await client.RegisterNewAsync();
        var second = await client.RegisterNewAsync();

        Assert.NotEqual(first.User.Id, second.User.Id);
    }

    [Fact]
    public async Task DuplicateUsernameAndEmailAreRejectedCaseInsensitively()
    {
        using var client = factory.CreateClient();
        var existing = await client.RegisterNewAsync();

        var sameUsername = await client.RegisterAsync(existing.User.Username.ToUpperInvariant(), email: $"other_{Guid.NewGuid():N}@example.com");
        var sameEmail = await client.RegisterAsync(IdentityTestClient.UniqueUsername(), email: existing.User.Email.ToUpperInvariant());

        Assert.Equal(HttpStatusCode.Conflict, sameUsername.StatusCode);
        Assert.Equal("identity.username_taken", await sameUsername.ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.Conflict, sameEmail.StatusCode);
        Assert.Equal("identity.email_taken", await sameEmail.ProblemCodeAsync());
    }

    [Theory]
    [InlineData("ab", "valid@example.com", "long enough password", "username")]
    [InlineData("admin", "valid@example.com", "long enough password", "username")]
    [InlineData("bad name!", "valid@example.com", "long enough password", "username")]
    [InlineData("valid_name", "not-an-email", "long enough password", "email")]
    [InlineData("valid_name", "valid@example.com", "short", "password")]
    public async Task InvalidRegistrationReturnsValidationProblem(string username, string email, string password, string field)
    {
        using var client = factory.CreateClient();

        var response = await client.RegisterAsync(username, email, password);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", await response.ProblemCodeAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task LoginAcceptsUsernameOrEmail()
    {
        using var client = factory.CreateClient();
        var registered = await client.RegisterNewAsync();

        var byUsername = await client.LoginAsync(registered.User.Username.ToUpperInvariant());
        var byEmail = await client.LoginAsync(registered.User.Email);

        Assert.Equal(HttpStatusCode.OK, byUsername.StatusCode);
        Assert.Equal(HttpStatusCode.OK, byEmail.StatusCode);
    }

    [Fact]
    public async Task WrongPasswordAndUnknownUserReturnTheSameError()
    {
        using var client = factory.CreateClient();
        var registered = await client.RegisterNewAsync();

        var wrongPassword = await client.LoginAsync(registered.User.Username, "wrong password value");
        var unknownUser = await client.LoginAsync(IdentityTestClient.UniqueUsername("ghost"));

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);
        Assert.Equal("identity.invalid_credentials", await wrongPassword.ProblemCodeAsync());
        Assert.Equal("identity.invalid_credentials", await unknownUser.ProblemCodeAsync());
    }

    [Fact]
    public async Task ProtectedEndpointRequiresToken()
    {
        using var client = factory.CreateClient();

        var anonymous = await client.GetAsync(new Uri("/users/me", UriKind.Relative));
        var forged = await client.SendAuthorizedAsync(HttpMethod.Get, "/users/me", "not.a.jwt");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
    }

    [Fact]
    public async Task LoginIsRateLimited()
    {
        using var limited = factory.WithWebHostBuilder(builder => builder.UseSetting("Identity:RateLimit:PermitLimit", "2"));
        using var client = limited.CreateClient();
        var login = IdentityTestClient.UniqueUsername("limited");

        await client.LoginAsync(login);
        await client.LoginAsync(login);
        var third = await client.LoginAsync(login);

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
    }
}
