using System.Reflection;

namespace Ovrilo.Api.System;

/// <summary>Detects the build-time OpenAPI document generator (Microsoft.Extensions.ApiDescription.Server).</summary>
internal static class BuildTimeOpenApi
{
    public const string PlaceholderSigningKey = "openapi-document-generation-placeholder-key";

    public static bool IsGenerating { get; } =
        string.Equals(Assembly.GetEntryAssembly()?.GetName().Name, "GetDocument.Insider", StringComparison.Ordinal);
}
