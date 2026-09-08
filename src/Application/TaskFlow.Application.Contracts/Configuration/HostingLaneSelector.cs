using Microsoft.Extensions.Configuration;

namespace TaskFlow.Application.Contracts.Configuration;

/// <summary>Hosting lane selected for this deployment; seeds the DEFAULT of every provider switch (D-035).</summary>
public enum HostingLane
{
    /// <summary>The existing full-Azure lane.</summary>
    Azure,

    /// <summary>Containers on a VPS; Azure retained only for Key Vault and App Configuration (D-044).</summary>
    Portable
}

/// <summary>
/// Resolves the active hosting lane: env <c>TASKFLOW_LANE</c> wins over <c>Hosting:Lane</c>, default Azure.
/// Lives in Application.Contracts - the lowest project both Infrastructure.Data (the Database provider
/// selector cannot reference Bootstrapper) and Infrastructure.AI (the Search provider selector) already
/// reference and that already carries Microsoft.Extensions.Configuration.Abstractions transitively - so
/// neither Infrastructure project takes on a dependency it would not otherwise have (an AI adapter must
/// not pull in the data layer for one enum parser). Bootstrapper's per-switch <c>LaneDefaults</c> map
/// (Registration/HostingLane.cs) reuses this same resolver instead of re-implementing lane parsing (D-035).
/// </summary>
public static class HostingLaneSelector
{
    public const string EnvironmentVariable = "TASKFLOW_LANE";
    public const string ConfigurationKey = "Hosting:Lane";

    public static HostingLane Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Parse(Environment.GetEnvironmentVariable(EnvironmentVariable) ?? configuration[ConfigurationKey]);
    }

    public static HostingLane ResolveFromEnvironment() =>
        Parse(Environment.GetEnvironmentVariable(EnvironmentVariable));

    private static HostingLane Parse(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? HostingLane.Azure
            : Enum.TryParse<HostingLane>(value, ignoreCase: true, out var lane)
                ? lane
                : throw new ArgumentException(
                    $"Unknown hosting lane '{value}'. Allowed values: {string.Join(", ", Enum.GetNames<HostingLane>())}.");
}
