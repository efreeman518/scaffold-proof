using System.Text.Json;
using TaskFlow.Domain.Shared;

namespace TaskFlow.Application.Contracts.Messaging;

/// <summary>
/// The single wire shape for every TaskFlow integration event (D-026). The broker carries <see cref="Id"/> as
/// MessageId, <see cref="Type"/> as Subject / routing key, and EventType / EventVersion / TenantId as message
/// properties, so a consumer can filter and dedup without deserializing <see cref="Payload"/>.
/// </summary>
/// <param name="Id">Message identity; also the outbox row id and the <c>ConsumerInbox.MessageId</c> (D-029).</param>
/// <param name="Type">CLR name of the payload record, e.g. <c>TaskItemCreatedEvent</c>.</param>
/// <param name="Version">Schema version of <see cref="Payload"/> for <see cref="Type"/>.</param>
/// <param name="TenantId">Owning tenant.</param>
/// <param name="OccurredAtUtc">When the domain change happened, not when it was dispatched.</param>
/// <param name="CorrelationId">Distributed-trace correlation of the originating operation, when there was one.</param>
/// <param name="Payload">The event record itself, serialized with default (PascalCase) naming.</param>
public sealed record IntegrationEventEnvelope(
    Guid Id,
    string Type,
    int Version,
    Guid TenantId,
    DateTimeOffset OccurredAtUtc,
    string? CorrelationId,
    JsonElement Payload)
{
    /// <summary>
    /// Per-type payload schema version. A record whose shape changes gets a bumped entry here and a consumer
    /// that switches on EventVersion; unlisted types are version 1.
    /// </summary>
    private static readonly Dictionary<string, int> Versions = new(StringComparer.Ordinal)
    {
        [nameof(Domain.Shared.Events.TaskItemCreatedEvent)] = 1,
        [nameof(Domain.Shared.Events.TaskItemStatusChangedEvent)] = 1,
        [nameof(Domain.Shared.Events.TaskItemCompletedEvent)] = 1,
        [nameof(Domain.Shared.Events.TaskItemOverdueSuspectedEvent)] = 1,
        [nameof(Domain.Shared.Events.TaskItemRescheduledEvent)] = 1,
        [nameof(Domain.Shared.Events.CommentAddedEvent)] = 1,
        [nameof(Domain.Shared.Events.AttachmentUploadedEvent)] = 1
    };

    /// <summary>Payload schema version for an event type name.</summary>
    public static int VersionFor(string eventType) => Versions.TryGetValue(eventType, out var version) ? version : 1;

    /// <summary>True when the event type is one this deployment knows how to consume.</summary>
    public static bool IsKnownType(string eventType) => Versions.ContainsKey(eventType);

    /// <summary>Wraps a raised domain event. <paramref name="id"/> allows a deterministic (replayable) message id.</summary>
    public static IntegrationEventEnvelope From(
        IDomainEvent domainEvent, DateTimeOffset occurredAtUtc, string? correlationId, Guid? id = null)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var type = domainEvent.GetType().Name;
        return new IntegrationEventEnvelope(
            id ?? Guid.CreateVersion7(),
            type,
            VersionFor(type),
            domainEvent.TenantId,
            occurredAtUtc,
            correlationId,
            JsonSerializer.SerializeToElement(domainEvent, domainEvent.GetType()));
    }
}
