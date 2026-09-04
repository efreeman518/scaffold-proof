namespace TaskFlow.Infrastructure.Data.Operational;

/// <summary>
/// Lease-claimable work row shared by the transactional outbox and blob-delete work tables (D-026). Not a tenant
/// entity: no query filter, no Version, schema <c>taskflow</c>. Claim/dispatch behavior lives with the scheduler workers.
/// </summary>
public abstract class OperationalWorkBase
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public DateTimeOffset AvailableAtUtc { get; set; }
    public Guid? LeaseToken { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresUtc { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? DeadLetteredAtUtc { get; set; }
}

/// <summary>Transactional outbox row staged in the same SaveChanges as the domain write (D-026).</summary>
public sealed class OutboxMessage : OperationalWorkBase
{
    public string Destination { get; set; } = null!;
    public string EventType { get; set; } = null!;
    public int EventVersion { get; set; }
    /// <summary>Serialized envelope. Plain string column: upgrade to jsonb (PostgreSQL) / json (SQL Server 2025) when queried.</summary>
    public string Payload { get; set; } = null!;
    public string? CorrelationId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}

/// <summary>Deferred blob deletion so a domain delete never waits on storage (404 counts as success).</summary>
public sealed class BlobDeleteWork : OperationalWorkBase
{
    public string ContainerName { get; set; } = null!;
    public string BlobName { get; set; } = null!;
}

/// <summary>Consumer idempotency record, insert-first inside the consumer's unit of work (D-029).</summary>
public sealed class ConsumerInbox
{
    public string Consumer { get; set; } = null!;
    public Guid MessageId { get; set; }
    public DateTimeOffset ProcessedAtUtc { get; set; }
}
