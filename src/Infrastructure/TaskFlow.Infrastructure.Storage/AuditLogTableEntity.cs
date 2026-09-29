using Azure;
using Azure.Data.Tables;

namespace TaskFlow.Infrastructure.Storage;

/// <summary>Provides audit log table entity behavior for the Infrastructure layer.</summary>
public class AuditLogTableEntity : ITableEntity
{
    public string PartitionKey { get; set; } = null!;
    public string RowKey { get; set; } = null!;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public Guid Id { get; set; }
    public string AuditId { get; set; } = null!;
    public string? TenantId { get; set; }
    public string EntityType { get; set; } = null!;
    public string EntityKey { get; set; } = null!;
    public string Action { get; set; } = null!;
    public string Status { get; set; } = null!;
    public DateTimeOffset StartedAtUtc { get; set; }
    public long ElapsedTimeTicks { get; set; }
    public DateTimeOffset RecordedUtc { get; set; }
    public string? Metadata { get; set; }
    public string? Error { get; set; }
}