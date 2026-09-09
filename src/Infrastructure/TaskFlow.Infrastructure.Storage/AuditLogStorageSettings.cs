using EF.Table;

namespace TaskFlow.Infrastructure.Storage;

/// <summary>Provides audit log storage behavior for the Infrastructure layer.</summary>
public class AuditLogStorageSettings : TableRepositorySettingsBase
{
    public const string ConfigSectionName = "AuditLogStorageSettings";

    public string TableName { get; set; } = "taskflowaudit";

    /// <summary>Partition prefix for entries with no tenant (system operations).</summary>
    public string NullTenantPartitionKey { get; set; } = "_system";

    /// <summary>Days of audit history kept by the AuditRetention job.</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>Initializes audit log storage settings with required dependencies and default state.</summary>
    public AuditLogStorageSettings()
    {
        TableServiceClientName = "TaskFlowTableClient";
    }
}
