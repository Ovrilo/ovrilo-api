using System.Reflection;

using Ovrilo.Api.System;
using Ovrilo.Modules.Identity;

var builder = WebApplication.CreateBuilder(args);

if (BuildTimeOpenApi.IsGenerating)
{
    // Build-time OpenAPI generation starts the host without secrets; nothing is signed or served.
    builder.Configuration["Identity:Jwt:SigningKey"] = BuildTimeOpenApi.PlaceholderSigningKey;
}

var apiVersion = typeof(Program).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddProblemDetails();
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

builder.Services.AddIdentityModule();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapHealthChecks("/health");

app.MapGet("/version", () => new VersionResponse(apiVersion, ApiContract.Version))
    .WithName("GetVersion")
    .WithTags("System")
    .WithSummary("Returns the API build version and contract version.");

app.MapIdentityEndpoints();

// Development and tests only. Production schema changes are explicit, authorized operations.
if (app.Configuration.GetValue<bool>("Database:ApplyMigrationsOnStartup"))
{
    await app.Services.MigrateIdentityDatabaseAsync();
}

await app.RunAsync();

public partial class Program;
