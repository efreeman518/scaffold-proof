using EF.Messaging;
using EF.Messaging.Outbox;
using TaskFlow.Domain.Shared;
using TaskFlow.Domain.Shared.Events;

namespace TaskFlow.Application.Contracts.Messaging;

/// <summary>
/// The event types TaskFlow publishes, and the one place a raised domain event becomes an
/// <see cref="IntegrationEventEnvelope"/> (D-026). The envelope frame itself is the package's: the broker
/// carries <c>Id</c> as MessageId and <c>Type</c> as Subject / routing key, so a consumer can filter and dedup
/// without deserializing the payload.
/// <para>
/// The envelope carries no tenant. TaskFlow's tenant travels in the payload (every
/// <see cref="TenantDomainEvent"/> has one) and as the <see cref="TenantIdHeader"/> outbox header, which the
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
    /// Envelope types a reader accepts. The reader sees only the wire name, so this is a name set; it lists every
    /// <see cref="TenantDomainEvent"/> record, which <c>JsonContextCompletenessTests</c> checks against the closed
    /// hierarchy, and a removed record fails the build here through its <c>nameof</c>.
    /// </summary>
    private static readonly HashSet<string> KnownTypes = new(StringComparer.Ordinal)
    {
        nameof(TaskItemCreatedEvent),
        nameof(TaskItemContentChangedEvent),
        nameof(TaskItemStatusChangedEvent),
        nameof(TaskItemCompletedEvent),
        nameof(TaskItemOverdueSuspectedEvent),
        nameof(TaskItemRescheduledEvent),
        nameof(CommentAddedEvent),
        nameof(AttachmentUploadedEvent)
    };

    /// <summary>
    /// Payload schema version of an event. A record whose shape changes gets a bumped arm here and a consumer
    /// that switches on EventVersion. There is no discard arm: <see cref="TenantDomainEvent"/> is closed, so a new
    /// event record does not compile until it is given a version here, which is also the place to add it to
    /// <see cref="KnownTypes"/> and to <see cref="TaskFlowMessagingJsonContext"/>.
    /// </summary>
    /// <param name="domainEvent">The raised event.</param>
    public static int VersionOf(TenantDomainEvent domainEvent) => domainEvent switch
    {
        TaskItemCreatedEvent => IntegrationEventEnvelope.InitialVersion,
        TaskItemContentChangedEvent => IntegrationEventEnvelope.InitialVersion,
        TaskItemStatusChangedEvent => IntegrationEventEnvelope.InitialVersion,
        TaskItemCompletedEvent => IntegrationEventEnvelope.InitialVersion,
        TaskItemOverdueSuspectedEvent => IntegrationEventEnvelope.InitialVersion,
        TaskItemRescheduledEvent => IntegrationEventEnvelope.InitialVersion,
        CommentAddedEvent => IntegrationEventEnvelope.InitialVersion,
        AttachmentUploadedEvent => IntegrationEventEnvelope.InitialVersion
    };

    /// <summary>True when the event type is one this deployment knows how to consume.</summary>
    public static bool IsKnownType(string eventType) => KnownTypes.Contains(eventType);

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
        TenantDomainEvent domainEvent, DateTimeOffset occurredAtUtc, string? correlationId, Guid? id = null)
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
                + "[JsonSerializable] entry for it alongside its VersionOf arm.");
        }

        return IntegrationEventEnvelope.From(
            domainEvent,
            occurredAtUtc,
            correlationId,
            VersionOf(domainEvent),
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
