using Microsoft.Extensions.Configuration;

namespace AppHost;

/// <summary>Hosting lane selected for this run; seeds the DEFAULT of every provider switch (D-035).</summary>
public enum HostingLane
{
    /// <summary>The existing full-Azure lane: emulators for Blob/Table, Cosmos, Service Bus, plus Functions.</summary>
    Azure,

    /// <summary>Containers only: Postgres, RabbitMQ, MinIO. Azure keeps Key Vault + App Configuration (D-044).</summary>
    Portable
}

/// <summary>
/// The provider switch values this AppHost run resolved, and the environment every host must receive so the
/// hosts agree with the containers the AppHost actually declared.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Database"/> and <see cref="Messaging"/> are always set because the AppHost has to pick exactly one
/// relational server and one broker to declare (D-020, D-034). Every other switch is nullable and stays null in
/// the Azure lane: "Azure" means "today's behavior", not a new fixed value - the AI, Search and DataProtection
/// defaults are derived at runtime from other settings, so writing a literal for them would change behavior
/// rather than preserve it. A switch's own env var or config key always wins over the lane default.
/// </para>
/// </remarks>
public sealed record LaneSwitches(
    HostingLane Lane,
    string Database,
    string Messaging,
    string? Storage,
    string? ReadModel,
    string? Audit,
    string? Search,
    string? AiServices,
    string? DataProtection)
{
    /// <summary>True when this run declares the portable container topology instead of the Azure emulators.</summary>
    public bool IsPortable => Lane == HostingLane.Portable;

    /// <summary>
    /// Environment every host receives so the lane and its resolved switches reach the app configuration the
    /// same way Bicep and the compose lane set them. <c>Database__Provider</c> and <c>Messaging__Provider</c>
    /// are written by their own pre-existing call sites and are deliberately not repeated here.
    /// </summary>
    public IReadOnlyDictionary<string, string> HostEnvironment { get; } = Build(
        Lane, Storage, ReadModel, Audit, Search, AiServices, DataProtection);

    private static Dictionary<string, string> Build(
        HostingLane lane,
        string? storage,
        string? readModel,
        string? audit,
        string? search,
        string? aiServices,
        string? dataProtection)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Hosting__Lane"] = lane.ToString()
        };

        Add("Storage__Provider", storage);
        Add("ReadModel__Provider", readModel);
        Add("Audit__Provider", audit);
        Add("Search__Provider", search);
        Add("AiServices__Provider", aiServices);
        Add("DataProtection__Persistence", dataProtection);
        return values;

        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                values[key] = value;
            }
        }
    }
}

/// <summary>
/// Resolves <c>TASKFLOW_LANE</c> and the per-switch defaults it seeds (D-035).
/// </summary>
/// <remarks>
/// <para>
/// The parsing rule is the same one <c>TaskFlow.Application.Contracts.Configuration.HostingLaneSelector</c>
/// applies inside the hosts - env <c>TASKFLOW_LANE</c> wins over config <c>Hosting:Lane</c>, default Azure,
/// unknown value throws - and it is restated here rather than referenced. An Aspire AppHost's project
/// references are resource declarations, not compile-time assembly references, so the AppHost cannot bind to
/// application types; the shared contract is the env var name and the parsing rule, and the portable topology
/// test pins both against the host-side selector.
/// </para>
/// <para>
/// Portable lane values match <c>TaskFlow.Bootstrapper.LaneDefaults.Portable</c>. The one deliberate
/// difference from <c>.scaffold/resource-implementation.yaml</c> is Search: the artifact records PgVector as
/// the portable default, and the Bootstrapper still defaults to Sql because the PgVector arm lands in slice
/// P7. Flip both together when it does.
/// </para>
/// </remarks>
public static class LaneDefaults
{
    public const string LaneEnvironmentVariable = "TASKFLOW_LANE";
    public const string LaneConfigurationKey = "Hosting:Lane";

    /// <summary>Resolves the lane, then every switch the lane seeds a default for.</summary>
    public static LaneSwitches Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var lane = ParseLane(Environment.GetEnvironmentVariable(LaneEnvironmentVariable)
            ?? configuration[LaneConfigurationKey]);
        var portable = lane == HostingLane.Portable;

        return new LaneSwitches(
            lane,
            // D-020 / D-034: the AppHost must declare exactly one server and one broker, so these two always
            // resolve to a concrete value - the Azure lane keeps the pre-existing SqlServer/ServiceBus defaults.
            Database: Switch(configuration, "TASKFLOW_DB_PROVIDER", "Database:Provider",
                portable ? "PostgreSql" : "SqlServer")!,
            Messaging: Switch(configuration, "TASKFLOW_MESSAGING_PROVIDER", "Messaging:Provider",
                portable ? "RabbitMq" : "ServiceBus")!,
            Storage: Switch(configuration, "TASKFLOW_STORAGE_PROVIDER", "Storage:Provider",
                portable ? "S3" : null),
            ReadModel: Switch(configuration, "TASKFLOW_READMODEL_PROVIDER", "ReadModel:Provider",
                portable ? "Relational" : null),
            Audit: Switch(configuration, "TASKFLOW_AUDIT_PROVIDER", "Audit:Provider",
                portable ? "Relational" : null),
            Search: Switch(configuration, "TASKFLOW_SEARCH_PROVIDER", "Search:Provider",
                portable ? "Sql" : null),
            AiServices: Switch(configuration, "TASKFLOW_AI_PROVIDER", "AiServices:Provider",
                portable ? "OpenAICompatible" : null),
            DataProtection: Switch(configuration, "TASKFLOW_DATAPROTECTION_PERSISTENCE", "DataProtection:Persistence",
                portable ? "Redis" : null));
    }

    private static string? Switch(IConfiguration configuration, string envVar, string configKey, string? laneDefault)
    {
        var explicitValue = Environment.GetEnvironmentVariable(envVar) ?? configuration[configKey];
        return string.IsNullOrWhiteSpace(explicitValue) ? laneDefault : explicitValue;
    }

    private static HostingLane ParseLane(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? HostingLane.Azure
            : Enum.TryParse<HostingLane>(value, ignoreCase: true, out var lane)
                ? lane
                : throw new ArgumentException(
                    $"Unknown hosting lane '{value}'. Allowed values: {string.Join(", ", Enum.GetNames<HostingLane>())}.");
}
