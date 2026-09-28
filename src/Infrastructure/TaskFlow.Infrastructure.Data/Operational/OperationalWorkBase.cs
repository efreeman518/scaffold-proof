namespace TaskFlow.Infrastructure.Data.Operational;

/// <summary>
/// Lease-claimable work row shared by the transactional outbox and blob-delete work tables (D-026). Not a tenant
/// entity: no query filter, no Version, schema <c>taskflow</c>. Claim/dispatch behavior lives with the scheduler workers;
/// the attempt ceiling is the worker's <c>LeasedWorkerOptions.MaxAttempts</c>, passed into every claim.
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
    /// <summary>
    /// W3C <c>traceparent</c> of the operation that staged the row (D-053). The dispatcher parents the producer
    /// span to it, so the consumer continues the request's trace instead of the Scheduler drain's.
    /// </summary>
    public string? TraceParent { get; set; }
    /// <summary>W3C <c>tracestate</c> captured with <see cref="TraceParent"/>; null when empty or over the column limit.</summary>
    public string? TraceState { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}

/// <summary>Deferred blob deletion so a domain delete never waits on storage (404 counts as success).</summary>
public sealed class BlobDeleteWork : OperationalWorkBase
{
    public string ContainerName { get; set; } = null!;
    public string BlobName { get; set; } = null!;
}

/// <summary>
/// Two-state consumer idempotency record (D-029). A claim is in progress while <see cref="CompletedAtUtc"/> is null
/// and its <see cref="LeaseExpiresUtc"/> is live; the consumer marks it completed only after its effect ran. An
/// in-progress claim whose lease expired (the consumer crashed or hung) is taken over by the next delivery.
/// </summary>
public sealed class ConsumerInbox
{
    public string Consumer { get; set; } = null!;
    public Guid MessageId { get; set; }
    /// <summary>Token of the delivery that owns the claim; complete and release are guarded by it.</summary>
    public Guid ClaimToken { get; set; }
    public DateTimeOffset ClaimedAtUtc { get; set; }
    /// <summary>End of the in-progress lease; null once completed.</summary>
    public DateTimeOffset? LeaseExpiresUtc { get; set; }
    /// <summary>Set when the consumer's effect ran; a completed claim turns every redelivery into a duplicate.</summary>
    public DateTimeOffset? CompletedAtUtc { get; set; }
}
