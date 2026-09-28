using System.Text.Json;
using System.Text.Json.Serialization;

namespace Test.Support;

/// <summary>
/// Shared JSON options for test-side serialization, mirroring the API host's
/// ConfigureHttpJsonOptions: <see cref="JsonSerializerDefaults.Web"/> (camelCase names, case-insensitive
/// reads, numbers readable from strings) plus string enums. Centralized so per-test options cannot drift
/// and mask contract regressions.
/// </summary>
public static class JsonTestOptions
{
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}
