using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TaskFlow.Application.Contracts.Storage;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Repositories;
using TaskFlow.Infrastructure.Storage;

namespace TaskFlow.Bootstrapper;

/// <summary>Audit-sink backend selected for this deployment (D-039).</summary>
public enum AuditProvider
{
    /// <summary>The existing Azure Table Storage audit sink.</summary>
    AzureTable,

    /// <summary>A relational AuditLog table in the application database.</summary>
    Relational
}

public static partial class RegisterServices
{
    public const string AuditProviderConfigKey = "Audit:Provider";
    public const string AuditProviderEnvVar = "TASKFLOW_AUDIT_PROVIDER";

    /// <summary>
    /// Resolves the audit-sink backend. The environment variable wins over configuration; when neither is
    /// set, the Portable lane defaults to Relational and the Azure lane keeps today's Azure Table default (D-035).
    /// </summary>
    public static AuditProvider ResolveAuditProvider(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var value = Environment.GetEnvironmentVariable(AuditProviderEnvVar) ?? config[AuditProviderConfigKey];
        if (!string.IsNullOrWhiteSpace(value)) return ParseAuditProvider(value);

        return HostingLaneSelector.Resolve(config) == HostingLane.Portable
            ? AuditProvider.Relational
            : AuditProvider.AzureTable;
    }

    private static AuditProvider ParseAuditProvider(string value) =>
        Enum.TryParse<AuditProvider>(value, ignoreCase: true, out var provider)
            ? provider
            : throw new ArgumentException(
                $"Unknown audit provider '{value}'. Allowed values: {string.Join(", ", Enum.GetNames<AuditProvider>())}.");

    /// <summary>Dispatches to the selected audit-sink backend.</summary>
    [ProviderSwitch(typeof(IAuditLogRepository))]
    private static void AddAuditServices(IServiceCollection services, IConfiguration config)
    {
        switch (ResolveAuditProvider(config))
        {
            case AuditProvider.AzureTable:
                AddTableStorageServices(services, config);
                break;
            case AuditProvider.Relational:
                AddRelationalAuditServices(services, config);
                break;
        }
    }

    /// <summary>
    /// Relational audit sink (D-039). The settings section is bound here as well as in the Table arm: it
    /// carries the retention window the Scheduler job reads and the sentinel tenant for entries with no
    /// tenant, neither of which is Table-specific. No extra health check: the always-on <c>sql</c> readiness
    /// check already covers this sink.
    /// </summary>
    private static void AddRelationalAuditServices(IServiceCollection services, IConfiguration config)
    {
        services.Configure<AuditLogStorageSettings>(
            config.GetSection(AuditLogStorageSettings.ConfigSectionName));

        services.AddScoped<IAuditLogRepository>(sp => new RelationalAuditLogRepository(
            sp.GetRequiredService<TaskFlowDbContextTrxn>(),
            sp.GetRequiredService<IOptions<AuditLogStorageSettings>>().Value.NullTenantPartitionKey));
    }
}
