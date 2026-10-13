using System.Reflection;

using Ovrilo.Api.System;

var builder = WebApplication.CreateBuilder(args);

var apiVersion = typeof(Program).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "Ovrilo API";
    document.Info.Version = ApiContract.Version;
    return Task.CompletedTask;
}));
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapHealthChecks("/health");

app.MapGet("/version", () => new VersionResponse(apiVersion, ApiContract.Version))
    .WithName("GetVersion")
    .WithTags("System")
    .WithSummary("Returns the API build version and contract version.");

app.Run();

public partial class Program;
