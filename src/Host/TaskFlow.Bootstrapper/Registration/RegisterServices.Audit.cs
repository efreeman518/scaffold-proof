using EF.Audit.Data;
using EF.Audit.AzureTable;
using EF.Common;
using EF.Audit.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TaskFlow.Application.Contracts;
using TaskFlow.Infrastructure.Data;

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
    public const string AuditProviderConfigKey = HostingLaneResolver.AuditConfigurationKey;
    public const string AuditProviderEnvVar = HostingLaneResolver.AuditEnvironmentVariable;

    /// <summary>
    /// Resolves the strict lane's audit-sink backend through the shared D-060 contract.
    /// </summary>
    public static AuditProvider ResolveAuditProvider(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return ParseAuditProvider(HostingLaneResolver.Resolve(config).Audit);
    }

    private static AuditProvider ParseAuditProvider(string value) =>
        StrictEnum.Parse<AuditProvider>(value, "audit provider");

    /// <summary>
    /// Section both arms read the shared audit settings (system-tenant sentinel, retention window, purge batch
    /// size) from, so switching arms changes no configuration.
    /// </summary>
    public const string AuditSettingsSection = AzureTableAuditLogSettings.ConfigSectionName + ":Audit";

    /// <summary>
    /// Dispatches to the selected audit-sink backend. The shared <see cref="AuditSettings"/> are bound for either
    /// arm: the Scheduler's retention job reads the window from them.
    /// </summary>
    [ProviderSwitch(typeof(IAuditLogRepository))]
    internal static void AddAuditServices(IServiceCollection services, IConfiguration config)
    {
        services.Configure<AuditSettings>(config.GetSection(AuditSettingsSection));

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
    /// Relational audit sink (D-039/D18): the EF.Audit.Data repository over the write context, which maps the
    /// audit row. It reads the same shared audit settings as the Table arm. No extra health check: the always-on
    /// <c>sql</c> readiness check already covers this sink.
    /// </summary>
    private static void AddRelationalAuditServices(IServiceCollection services, IConfiguration config) =>
        services.AddRelationalAuditLog<TaskFlowDbContextTrxn>(
            options => config.GetSection(AuditSettingsSection).Bind(options.Audit));
}
