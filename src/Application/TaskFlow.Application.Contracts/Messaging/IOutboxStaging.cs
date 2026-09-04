using System.Text.Json;

namespace TaskFlow.Application.Contracts.Messaging;

// temporary: S6 owns the real IOutboxStaging and IntegrationEventEnvelope; delete on merge.
// Shape copied verbatim from the plan (3.1 staging, 3.2 envelope) so the scheduler jobs below compile
// and behave identically once the messaging slice lands.

/// <summary>
/// Transport-neutral integration event envelope. <c>Id</c> is the broker MessageId and the outbox row id,
/// so a deterministic id makes a replayed producer a no-op instead of a duplicate publish.
/// </summary>
public sealed record IntegrationEventEnvelope(
    Guid Id,
    string Type,
    int Version,
    Guid TenantId,
    DateTimeOffset OccurredAtUtc,
    string? CorrelationId,
    JsonElement Payload);

/// <summary>
/// Stages an integration event on the current unit of work for callers that have no tracked aggregate to
/// hang a domain event on - the <c>ExecuteUpdate</c> paths in the scheduler jobs. The row is written by the
/// same <c>SaveChanges</c> as the data change, so an event can never escape a rolled-back transaction.
/// </summary>
public interface IOutboxStaging
{
    /// <summary>
    /// Stages one event. <paramref name="deterministicId"/> is the outbox row id when the caller can derive a
    /// stable one (UUIDv5 over the natural key); a repeat of the same logical event then collapses onto the
    /// same row instead of producing a second copy.
    /// </summary>
    void Stage(IntegrationEventEnvelope envelope, Guid? deterministicId = null);
}
