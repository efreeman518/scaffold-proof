using Microsoft.Extensions.Configuration;
using TaskFlow.Hosting;

namespace AppHost;

/// <summary>AppHost adapter over the shared D-060 hosting contract.</summary>
public sealed record LaneSwitches(
    HostingLane Lane,
    string Database,
    string Messaging,
    string Storage,
    string ReadModel,
    string Audit,
    string Search,
    string AiServices,
    string DataProtection)
{
    // Compatibility for the existing graph. S4 owns topology terminology and resource changes.
    public bool IsPortable => Lane == HostingLane.NonAzure;

    public IReadOnlyDictionary<string, string> HostEnvironment { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Hosting__Lane"] = Lane.ToString(),
        ["Storage__Provider"] = Storage,
        ["ReadModel__Provider"] = ReadModel,
        ["Audit__Provider"] = Audit,
        ["Search__Provider"] = Search,
        ["AiServices__Provider"] = AiServices,
        ["DataProtection__Persistence"] = DataProtection
    };
}

/// <summary>Thin AppHost adapter so the resource graph consumes the same resolver as runtime hosts.</summary>
public static class LaneDefaults
{
    public const string LaneEnvironmentVariable = HostingLaneResolver.LaneEnvironmentVariable;
    public const string LaneConfigurationKey = HostingLaneResolver.LaneConfigurationKey;

    public static LaneSwitches Resolve(IConfiguration configuration)
    {
        var settings = HostingLaneResolver.Resolve(configuration);
        return new LaneSwitches(
            settings.Lane,
            settings.Database,
            settings.Messaging,
            settings.Storage,
            settings.ReadModel,
            settings.Audit,
            settings.Search,
            settings.AiServices,
            settings.DataProtection);
    }
}
