using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

using Ovrilo.Api.System;
using Ovrilo.Modules.Channels;
using Ovrilo.Modules.Communities;
using Ovrilo.Modules.Identity;
using Ovrilo.Modules.VoiceSessions;

var builder = WebApplication.CreateBuilder(args);

if (BuildTimeOpenApi.IsGenerating)
{
    // Build-time OpenAPI generation starts the host without secrets; nothing is signed or served.
    builder.Configuration["Identity:Jwt:SigningKey"] = BuildTimeOpenApi.PlaceholderSigningKey;
    builder.Configuration["VoiceSessions:LiveKit:ServerUrl"] = "ws://localhost";
    builder.Configuration["VoiceSessions:LiveKit:ApiKey"] = "openapi";
    builder.Configuration["VoiceSessions:LiveKit:ApiSecret"] = BuildTimeOpenApi.PlaceholderSigningKey;
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
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
    .WithHeaders("Authorization", "Content-Type")
    .WithMethods("GET", "POST", "PATCH", "DELETE")
    .SetPreflightMaxAge(TimeSpan.FromHours(1))));

builder.Services.AddIdentityModule();
builder.Services.AddCommunitiesModule();
builder.Services.AddChannelsModule();
builder.Services.AddVoiceSessionsModule();

var app = builder.Build();

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

// Development and tests only. Production schema changes are explicit, authorized operations.
if (app.Configuration.GetValue<bool>("Database:ApplyMigrationsOnStartup"))
{
    await app.Services.MigrateIdentityDatabaseAsync();
    await app.Services.MigrateCommunitiesDatabaseAsync();
    await app.Services.MigrateChannelsDatabaseAsync();
}

await app.RunAsync();

public partial class Program;
