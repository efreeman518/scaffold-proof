using EF.Audit.Contracts;
using EF.Table;

namespace TaskFlow.Infrastructure.Storage;

/// <summary>
/// Settings for the audit sink. The Table-specific half (client name, table name) lives here; the half
/// both arms share - system-tenant sentinel, retention window, purge batch size - is
/// <see cref="EF.Audit.Contracts.AuditSettings"/>, bound as a nested <c>Audit</c> section so the relational
/// arm reads exactly the same values.
/// </summary>
public class AuditLogStorageSettings : TableRepositorySettingsBase
{
    public const string ConfigSectionName = "AuditLogStorageSettings";

    public string TableName { get; set; } = "taskflowaudit";

    /// <summary>Shared audit settings: system-tenant sentinel, retention window, and purge batch size.</summary>
    public AuditSettings Audit { get; set; } = new();

    /// <summary>Initializes audit log storage settings with required dependencies and default state.</summary>
    public AuditLogStorageSettings()
    {
        TableServiceClientName = "TaskFlowTableClient";
    }
}
