using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.SignalR;

using Ovrilo.Api.Realtime;
using Ovrilo.Api.System;
using Ovrilo.BuildingBlocks.Realtime;
using Ovrilo.Modules.Channels;
using Ovrilo.Modules.Communities;
using Ovrilo.Modules.Identity;
using Ovrilo.Modules.Media;
using Ovrilo.Modules.Messaging;
using Ovrilo.Modules.VoiceSessions;

// `migrate` applies pending schema migrations and exits: an explicit, authorized operation for
// production (ADR-0013), instead of migrating on every start.
const string MigrateCommand = "migrate";
var migrateOnly = args.Contains(MigrateCommand);

var builder = WebApplication.CreateBuilder(args.Where(a => a != MigrateCommand).ToArray());

if (BuildTimeOpenApi.IsGenerating)
{
    // Build-time OpenAPI generation starts the host without secrets; nothing is signed or served.
    builder.Configuration["Identity:Jwt:SigningKey"] = BuildTimeOpenApi.PlaceholderSigningKey;
    builder.Configuration["VoiceSessions:LiveKit:ServerUrl"] = "ws://localhost";
    builder.Configuration["VoiceSessions:LiveKit:ApiKey"] = "openapi";
    builder.Configuration["VoiceSessions:LiveKit:ApiSecret"] = BuildTimeOpenApi.PlaceholderSigningKey;
    builder.Configuration["Media:Janitor:Enabled"] = "false";
}

var apiVersion = typeof(Program).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    // Strict numbers so the contract says "integer", not "integer | string".
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
});
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "Ovrilo API";
        document.Info.Version = ApiContract.Version;
        return Task.CompletedTask;
    });
    options.AddBearerSecurity();
});
builder.Services.AddHealthChecks();

// Behind a TLS-terminating reverse proxy (Coolify/Traefik) the client address arrives in
// X-Forwarded-For; rate limiting depends on it. Enable only when the API port is reachable solely
// through the proxy. ForwardLimit = 1 uses the entry the proxy appended, ignoring client-supplied ones.
var behindReverseProxy = builder.Configuration.GetValue<bool>("ReverseProxy:Enabled");
if (behindReverseProxy)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });
}
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
    // x-requested-with / x-signalr-user-agent: SignalR negotiate and long-polling requests.
    .WithHeaders("Authorization", "Content-Type", "X-Requested-With", "X-SignalR-User-Agent")
    .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")
    // Clients show a cooldown from it after 429 Too Many Requests.
    .WithExposedHeaders("Retry-After")
    .SetPreflightMaxAge(TimeSpan.FromHours(1))));

builder.Services.AddIdentityModule();
builder.Services.AddCommunitiesModule();
builder.Services.AddChannelsModule();
builder.Services.AddVoiceSessionsModule();
builder.Services.AddMessagingModule();
builder.Services.AddMediaModule();

// Tell rejected clients when to retry (whole seconds, rounded up) so they can show a cooldown.
builder.Services.AddRateLimiter(options => options.OnRejected = (context, _) =>
{
    if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
    {
        context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
    }

    return ValueTask.CompletedTask;
});

// Realtime (ADR-0007): one hub; modules publish through IRealtimePublisher after committing.
builder.Services.AddSignalR(options => options.MaximumReceiveMessageSize = 4 * 1024)
    .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(
        new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false)));
builder.Services.AddSingleton<IRealtimePublisher, SignalRRealtimePublisher>();
builder.Services.AddSingleton<PresenceTracker>();
builder.Services.AddSingleton<IUserIdProvider, RealtimeUserIdProvider>();
// Browsers cannot set headers on WebSockets: accept the access token from the query string,
// but only for hub requests.
builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
{
    options.Events ??= new JwtBearerEvents();
    options.Events.OnMessageReceived = context =>
    {
        if (context.HttpContext.Request.Path.StartsWithSegments("/hubs", StringComparison.Ordinal)
            && context.Request.Query.TryGetValue("access_token", out var token))
        {
            context.Token = token;
        }

        return Task.CompletedTask;
    };
});

var app = builder.Build();

if (behindReverseProxy)
{
    app.UseForwardedHeaders();
}

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapHealthChecks("/health");

app.MapGet("/version", () => new VersionResponse(apiVersion, ApiContract.Version))
    .WithName("GetVersion")
    .WithTags("System")
    .WithSummary("Returns the API build version and contract version.");

app.MapIdentityEndpoints();
app.MapCommunitiesEndpoints();
app.MapChannelsEndpoints();
app.MapVoiceSessionsEndpoints();
app.MapMessagingEndpoints();
app.MapMediaEndpoints();
// Close connections when the access token expires, so revoked sessions stop receiving events;
// clients reconnect with a fresh token and catch up over REST.
app.MapHub<RealtimeHub>(RealtimeHub.Path, options => options.CloseOnAuthenticationExpiration = true);

// On startup in development and tests only; production uses the explicit `migrate` command.
if (migrateOnly || app.Configuration.GetValue<bool>("Database:ApplyMigrationsOnStartup"))
{
    await app.Services.MigrateIdentityDatabaseAsync();
    await app.Services.MigrateCommunitiesDatabaseAsync();
    await app.Services.MigrateChannelsDatabaseAsync();
    await app.Services.MigrateMessagingDatabaseAsync();
    await app.Services.MigrateMediaDatabaseAsync();
}

if (migrateOnly)
{
    Console.WriteLine("Database migrations applied.");
    return;
}

await app.RunAsync();

public partial class Program;
