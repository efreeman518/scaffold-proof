using EF.Messaging;
using EF.Messaging.Outbox;
using TaskFlow.Domain.Shared;

namespace TaskFlow.Application.Contracts.Messaging;

/// <summary>
/// The event types TaskFlow publishes, and the one place a raised domain event becomes an
/// <see cref="IntegrationEventEnvelope"/> (D-026). The envelope frame itself is the package's: the broker
/// carries <c>Id</c> as MessageId and <c>Type</c> as Subject / routing key, so a consumer can filter and dedup
/// without deserializing the payload.
/// <para>
/// The envelope carries no tenant. TaskFlow's tenant travels in the payload (every
/// <see cref="ITenantDomainEvent"/> has one) and as the <see cref="TenantIdHeader"/> outbox header, which the
/// transport copies onto the broker message, where a subscription rule and a partition key actually read it.
/// </para>
/// </summary>
public static class TaskFlowIntegrationEvents
{
    /// <summary>Logical channel every TaskFlow integration event goes to; the transport maps it to a topic or exchange.</summary>
    public const string Destination = "DomainEvents";

    /// <summary>Outbox header, and broker message property, carrying the owning tenant.</summary>
    public const string TenantIdHeader = "TenantId";
    /// <summary>
    /// Per-type payload schema version. A record whose shape changes gets a bumped entry here and a consumer
    /// that switches on EventVersion; unlisted types are version 1.
    /// </summary>
    private static readonly Dictionary<string, int> Versions = new(StringComparer.Ordinal)
    {
        [nameof(Domain.Shared.Events.TaskItemCreatedEvent)] = IntegrationEventEnvelope.InitialVersion,
        [nameof(Domain.Shared.Events.TaskItemContentChangedEvent)] = IntegrationEventEnvelope.InitialVersion,
        [nameof(Domain.Shared.Events.TaskItemStatusChangedEvent)] = IntegrationEventEnvelope.InitialVersion,
        [nameof(Domain.Shared.Events.TaskItemCompletedEvent)] = IntegrationEventEnvelope.InitialVersion,
        [nameof(Domain.Shared.Events.TaskItemOverdueSuspectedEvent)] = IntegrationEventEnvelope.InitialVersion,
        [nameof(Domain.Shared.Events.TaskItemRescheduledEvent)] = IntegrationEventEnvelope.InitialVersion,
        [nameof(Domain.Shared.Events.CommentAddedEvent)] = IntegrationEventEnvelope.InitialVersion,
        [nameof(Domain.Shared.Events.AttachmentUploadedEvent)] = IntegrationEventEnvelope.InitialVersion
    };

    /// <summary>Payload schema version for an event type name.</summary>
    public static int VersionFor(string eventType) =>
        Versions.TryGetValue(eventType, out var version) ? version : IntegrationEventEnvelope.InitialVersion;

    /// <summary>True when the event type is one this deployment knows how to consume.</summary>
    public static bool IsKnownType(string eventType) => Versions.ContainsKey(eventType);

    /// <summary>
    /// The one reader configuration every transport uses (D-034, D-048): envelopes deserialize through the
    /// generated messaging context, and a type this build has no consumer for is dead-lettered as unsupported.
    /// </summary>
    /// <param name="options">Options bound for <see cref="IntegrationEnvelopeReader"/>.</param>
    public static void ConfigureReader(IntegrationEnvelopeReaderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.SerializerOptions = TaskFlowMessagingJsonContext.Default.Options;
        options.IsKnownType = IsKnownType;
    }

    /// <summary>Wraps a raised domain event. <paramref name="id"/> allows a deterministic (replayable) message id.</summary>
    /// <param name="domainEvent">The raised event; its runtime type supplies the envelope type and payload.</param>
    /// <param name="occurredAtUtc">When the domain change happened, not when it was dispatched.</param>
    /// <param name="correlationId">Distributed-trace correlation of the originating operation, when there was one.</param>
    /// <param name="id">Message id; a new sortable GUID when omitted.</param>
    /// <exception cref="InvalidOperationException">
    /// The event record is not registered on <see cref="TaskFlowMessagingJsonContext"/>.
    /// </exception>
    public static IntegrationEventEnvelope Envelope(
        ITenantDomainEvent domainEvent, DateTimeOffset occurredAtUtc, string? correlationId, Guid? id = null)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var eventType = domainEvent.GetType();

        // D-048: the payload is serialized through the generated context. A type missing from it is a
        // build-time omission, not a runtime condition, so it fails here with the fix in the message rather
        // than deeper inside the serializer or - worse - through a reflection fallback nobody notices.
        if (TaskFlowMessagingJsonContext.Default.GetTypeInfo(eventType) is null)
        {
            throw new InvalidOperationException(
                $"{eventType.Name} is not registered on TaskFlowMessagingJsonContext (D-048). Add a "
                + "[JsonSerializable] entry for it alongside its Versions entry.");
        }

        return IntegrationEventEnvelope.From(
            domainEvent,
            occurredAtUtc,
            correlationId,
            VersionFor(eventType.Name),
            id,
            TaskFlowMessagingJsonContext.Default.Options);
    }

    /// <summary>
    /// The outbox entry for an envelope: sent to <see cref="Destination"/> with the tenant as the
    /// <see cref="TenantIdHeader"/> header. A job that re-runs sets a deterministic envelope id first, so the
    /// consumer inbox rejects the replay as a duplicate.
    /// </summary>
    /// <param name="envelope">Envelope to stage; its id becomes the outbox row id and the broker message id.</param>
    /// <param name="tenantId">Owning tenant.</param>
    public static OutboxEntry Entry(IntegrationEventEnvelope envelope, Guid tenantId) =>
        new(envelope, Destination, new Dictionary<string, string>(StringComparer.Ordinal) { [TenantIdHeader] = tenantId.ToString() });
}
