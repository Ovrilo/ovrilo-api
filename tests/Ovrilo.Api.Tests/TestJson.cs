using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ovrilo.Api.Tests;

/// <summary>Matches the API's wire format (camelCase properties and enum values).</summary>
internal static class TestJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
