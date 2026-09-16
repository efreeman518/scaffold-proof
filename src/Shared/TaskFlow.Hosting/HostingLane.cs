using Microsoft.Extensions.Configuration;

namespace TaskFlow.Hosting;

/// <summary>Provider and host boundary selected for a TaskFlow deployment (D-060).</summary>
public enum HostingLane
{
    Azure,
    NonAzure
}

/// <summary>Canonical provider selections resolved for one hosting lane.</summary>
public sealed record HostingLaneSettings(
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
    public bool IsNonAzure => Lane == HostingLane.NonAzure;
}

/// <summary>Canonical configuration names and strict lane resolver shared by every host and provider selector.</summary>
public static class HostingLaneResolver
{
    public const string LaneEnvironmentVariable = "TASKFLOW_LANE";
    public const string LaneConfigurationKey = "Hosting:Lane";

    public const string DatabaseEnvironmentVariable = "TASKFLOW_DB_PROVIDER";
    public const string DatabaseConfigurationKey = "Database:Provider";
    public const string MessagingEnvironmentVariable = "TASKFLOW_MESSAGING_PROVIDER";
    public const string MessagingConfigurationKey = "Messaging:Provider";
    public const string StorageEnvironmentVariable = "TASKFLOW_STORAGE_PROVIDER";
    public const string StorageConfigurationKey = "Storage:Provider";
    public const string ReadModelEnvironmentVariable = "TASKFLOW_READMODEL_PROVIDER";
    public const string ReadModelConfigurationKey = "ReadModel:Provider";
    public const string AuditEnvironmentVariable = "TASKFLOW_AUDIT_PROVIDER";
    public const string AuditConfigurationKey = "Audit:Provider";
    public const string SearchEnvironmentVariable = "TASKFLOW_SEARCH_PROVIDER";
    public const string SearchConfigurationKey = "Search:Provider";
    public const string AiEnvironmentVariable = "TASKFLOW_AI_PROVIDER";
    public const string AiConfigurationKey = "AiServices:Provider";
    public const string DataProtectionEnvironmentVariable = "TASKFLOW_DATAPROTECTION_PERSISTENCE";
    public const string DataProtectionConfigurationKey = "DataProtection:Persistence";

    public const string AppConfigEndpointConfigurationKey = "AppConfig:Endpoint";
    public const string AppConfigConnectionStringConfigurationKey = "ConnectionStrings:AppConfig";
    public const string KeyVaultEndpointConfigurationKey = "KeyVault:Endpoint";
    public const string KeyVaultUriConfigurationKey = "KeyVault:Uri";
    public const string DataProtectionEncryptionKeyUrlConfigurationKey = "DataProtectionEncryptionKeyUrl";

    public const string AppConfigEndpointEnvironmentVariable = "AppConfig__Endpoint";
    public const string AppConfigConnectionStringEnvironmentVariable = "ConnectionStrings__AppConfig";
    public const string KeyVaultEndpointEnvironmentVariable = "KeyVault__Endpoint";
    public const string KeyVaultUriEnvironmentVariable = "KeyVault__Uri";
    public const string DataProtectionEncryptionKeyUrlEnvironmentVariable = "DataProtectionEncryptionKeyUrl";

    private static readonly string[] DatabaseValues = ["SqlServer", "PostgreSql"];
    private static readonly string[] MessagingValues = ["ServiceBus", "RabbitMq"];
    private static readonly string[] StorageValues = ["AzureBlob", "S3"];
    private static readonly string[] ReadModelValues = ["Cosmos", "PostgreSqlJsonb", "MongoDb"];
    private static readonly string[] AuditValues = ["AzureTable", "Relational"];
    private static readonly string[] SearchValues = ["AzureAiSearch", "PgVector", "Sql"];
    private static readonly string[] AiValues = ["AzureInference", "OpenAICompatible", "None"];
    private static readonly string[] DataProtectionValues = ["AzureBlob", "Redis", "None"];

    public static HostingLaneSettings Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return ResolveCore(key => configuration[key]);
    }

    public static HostingLaneSettings ResolveFromEnvironment() =>
        ResolveCore(key => Environment.GetEnvironmentVariable(ToEnvironmentVariable(key)));

    public static HostingLane ResolveLane(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return ParseLane(Environment.GetEnvironmentVariable(LaneEnvironmentVariable) ?? configuration[LaneConfigurationKey]);
    }

    public static HostingLane ResolveLaneFromEnvironment() =>
        ParseLane(Environment.GetEnvironmentVariable(LaneEnvironmentVariable));

    private static HostingLaneSettings ResolveCore(Func<string, string?> configuration)
    {
        var lane = ParseLane(Environment.GetEnvironmentVariable(LaneEnvironmentVariable)
            ?? configuration(LaneConfigurationKey));

        if (lane == HostingLane.NonAzure)
        {
            RejectAzureServiceConfiguration(configuration, AppConfigEndpointConfigurationKey);
            RejectAzureServiceConfiguration(
                configuration, AppConfigConnectionStringConfigurationKey, redactValue: true);
            RejectAzureServiceConfiguration(configuration, KeyVaultEndpointConfigurationKey);
            RejectAzureServiceConfiguration(configuration, KeyVaultUriConfigurationKey);
            RejectAzureServiceConfiguration(configuration, DataProtectionEncryptionKeyUrlConfigurationKey);
        }

        var azure = lane == HostingLane.Azure;
        return new HostingLaneSettings(
            lane,
            ResolveProvider(configuration, lane, DatabaseEnvironmentVariable, DatabaseConfigurationKey,
                azure ? "SqlServer" : "PostgreSql", DatabaseValues,
                azure ? ["SqlServer"] : ["PostgreSql"]),
            ResolveProvider(configuration, lane, MessagingEnvironmentVariable, MessagingConfigurationKey,
                azure ? "ServiceBus" : "RabbitMq", MessagingValues,
                azure ? ["ServiceBus"] : ["RabbitMq"]),
            ResolveProvider(configuration, lane, StorageEnvironmentVariable, StorageConfigurationKey,
                azure ? "AzureBlob" : "S3", StorageValues,
                azure ? ["AzureBlob"] : ["S3"]),
            ResolveProvider(configuration, lane, ReadModelEnvironmentVariable, ReadModelConfigurationKey,
                azure ? "Cosmos" : "PostgreSqlJsonb", ReadModelValues,
                azure ? ["Cosmos"] : ["PostgreSqlJsonb", "MongoDb"],
                value => value.Equals("Relational", StringComparison.OrdinalIgnoreCase) ? "PostgreSqlJsonb" : value),
            ResolveProvider(configuration, lane, AuditEnvironmentVariable, AuditConfigurationKey,
                azure ? "AzureTable" : "Relational", AuditValues,
                azure ? ["AzureTable"] : ["Relational"]),
            ResolveProvider(configuration, lane, SearchEnvironmentVariable, SearchConfigurationKey,
                "Sql", SearchValues,
                azure ? ["Sql", "AzureAiSearch"] : ["Sql", "PgVector"]),
            ResolveProvider(configuration, lane, AiEnvironmentVariable, AiConfigurationKey,
                "None", AiValues,
                azure ? ["None", "AzureInference"] : ["None", "OpenAICompatible"]),
            ResolveProvider(configuration, lane, DataProtectionEnvironmentVariable, DataProtectionConfigurationKey,
                azure ? "AzureBlob" : "Redis", DataProtectionValues,
                azure ? ["AzureBlob"] : ["Redis"]));
    }

    private static HostingLane ParseLane(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return HostingLane.Azure;
        var normalized = value.Trim();
        if (normalized.Equals("Azure", StringComparison.OrdinalIgnoreCase)) return HostingLane.Azure;
        if (normalized.Equals("NonAzure", StringComparison.OrdinalIgnoreCase)) return HostingLane.NonAzure;
        if (normalized.Equals("Portable", StringComparison.OrdinalIgnoreCase)) return HostingLane.NonAzure;

        throw new ArgumentException(
            $"Unknown hosting lane '{value}'. Allowed values: Azure, NonAzure. Portable is a deprecated alias for NonAzure.");
    }

    private static string ToEnvironmentVariable(string configurationKey) =>
        configurationKey.Replace(":", "__", StringComparison.Ordinal);

    private static string ResolveProvider(
        Func<string, string?> configuration,
        HostingLane lane,
        string environmentVariable,
        string configurationKey,
        string laneDefault,
        IReadOnlyList<string> knownValues,
        IReadOnlyList<string> allowedValues,
        Func<string, string>? normalize = null)
    {
        var configured = Environment.GetEnvironmentVariable(environmentVariable) ?? configuration(configurationKey);
        if (string.IsNullOrWhiteSpace(configured)) return laneDefault;

        var normalized = normalize?.Invoke(configured) ?? configured;
        var canonical = knownValues.FirstOrDefault(value => value.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (canonical is null)
        {
            throw new ArgumentException(
                $"Hosting lane '{lane}' setting '{configurationKey}' has unknown configured value '{configured}'. " +
                $"Allowed values for this lane: {string.Join(", ", allowedValues)}.");
        }

        if (!allowedValues.Contains(canonical, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Hosting lane '{lane}' setting '{configurationKey}' has incompatible configured value '{configured}'. " +
                $"Allowed values for this lane: {string.Join(", ", allowedValues)}.");
        }

        return canonical;
    }

    private static void RejectAzureServiceConfiguration(
        Func<string, string?> configuration,
        string configurationKey,
        bool redactValue = false)
    {
        var environmentValue = Environment.GetEnvironmentVariable(ToEnvironmentVariable(configurationKey));
        var value = !string.IsNullOrWhiteSpace(environmentValue)
            ? environmentValue
            : configuration(configurationKey);
        if (string.IsNullOrWhiteSpace(value)) return;

        throw new InvalidOperationException(
            $"Hosting lane 'NonAzure' setting '{configurationKey}' has incompatible configured value " +
            $"'{(redactValue ? "<redacted>" : value)}'. " +
            "Allowed value for this lane: empty.");
    }
}
