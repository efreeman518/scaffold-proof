using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;
using TaskFlow.Application.Contracts.Messaging;
using TaskFlow.Application.Contracts.Services;
using TaskFlow.Domain.Shared.Events;
using TaskFlow.Observability.Meters;

namespace TaskFlow.Application.MessageHandlers.Consumers;

/// <summary>
/// Shared consumer logic behind both transports (D-034): the Azure Functions Service Bus triggers and the
/// RabbitMQ handler wrappers call these, so provider choice never changes behavior. Malformed and unsupported
/// envelopes are rejected by the transport-side reader before a consumer is reached; a consumer that throws is
/// a transient failure and the transport redelivers.
/// </summary>
/// <remarks>
/// Idempotency is the D-029 inbox: claim first so a redelivery short-circuits, release the claim if the work
/// throws so the redelivery is actually retried. A duplicate-PK rollback would have to read provider-specific
/// exception shapes (SqlException 2627 vs PostgresException 23505), which D-030 rules out.
/// </remarks>
public abstract class IntegrationEventConsumer(IInboxStore inbox, MessagingMetrics metrics, ILogger logger)
{
    /// <summary>Inbox consumer name; also the Service Bus subscription and RabbitMQ queue suffix.</summary>
    public abstract string ConsumerName { get; }

    /// <summary>True when this consumer acts on the envelope's event type at all.</summary>
    public abstract bool Handles(string eventType);

    /// <summary>Runs the consumer's own work for an envelope it has claimed.</summary>
    protected abstract Task ConsumeAsync(IntegrationEventEnvelope envelope, CancellationToken ct);

    /// <summary>Claims, consumes and records; releases the claim when the work throws.</summary>
    public async Task HandleAsync(IntegrationEventEnvelope envelope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!Handles(envelope.Type)) return;

        if (!await inbox.TryClaimAsync(ConsumerName, envelope.Id, ct).ConfigureAwait(false))
        {
            metrics.RecordInboxDuplicate(ConsumerName);
            logger.ConsumerDuplicateSkipped(ConsumerName, envelope.Type, envelope.Id);
            return;
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            await ConsumeAsync(envelope, ct).ConfigureAwait(false);
        }
        catch
        {
            await inbox.ReleaseAsync(ConsumerName, envelope.Id, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        metrics.RecordConsumerDuration(ConsumerName, Stopwatch.GetElapsedTime(started));
    }

    /// <summary>Reads a required Guid property from the envelope payload.</summary>
    protected static Guid PayloadGuid(IntegrationEventEnvelope envelope, string property) =>
        envelope.Payload.TryGetProperty(property, out var value) && value.TryGetGuid(out var id)
            ? id
            : throw new InvalidOperationException($"Envelope {envelope.Id} ({envelope.Type}) has no {property}.");
}

/// <summary>
/// Rebuilds the Cosmos TaskView read model. A full re-projection runs only on create and status change; the
/// delta events adjust counters in place so a busy task does not re-read its whole graph per comment.
/// </summary>
public sealed class TaskProjectionConsumer(
    IInboxStore inbox,
    ITaskViewProjectionService projection,
    MessagingMetrics metrics,
    ILogger<TaskProjectionConsumer> logger) : IntegrationEventConsumer(inbox, metrics, logger)
{
    /// <summary>Subscription and queue name for this consumer.</summary>
    public const string Name = "projection";

    /// <inheritdoc />
    public override string ConsumerName => Name;

    /// <inheritdoc />
    public override bool Handles(string eventType) => eventType is
        nameof(TaskItemCreatedEvent) or nameof(TaskItemStatusChangedEvent) or nameof(TaskItemCompletedEvent)
        or nameof(CommentAddedEvent) or nameof(AttachmentUploadedEvent);

    /// <inheritdoc />
    protected override Task ConsumeAsync(IntegrationEventEnvelope envelope, CancellationToken ct) => envelope.Type switch
    {
        nameof(CommentAddedEvent) => projection.AdjustCountersAsync(
            PayloadGuid(envelope, nameof(CommentAddedEvent.TaskItemId)), envelope.TenantId,
            new TaskViewCounterDelta(CommentCount: 1), envelope.OccurredAtUtc, ct),
        nameof(AttachmentUploadedEvent) => projection.AdjustCountersAsync(
            PayloadGuid(envelope, nameof(AttachmentUploadedEvent.OwnerId)), envelope.TenantId,
            new TaskViewCounterDelta(AttachmentCount: 1), envelope.OccurredAtUtc, ct),
        _ => projection.ProjectTaskItemAsync(
            PayloadGuid(envelope, nameof(TaskItemCreatedEvent.TaskItemId)), envelope.OccurredAtUtc, ct)
    };
}

/// <summary>Runs the D6 AI readiness review for a newly created task.</summary>
public sealed class TaskAiReviewConsumer(
    IInboxStore inbox,
    IAiTaskReviewer reviewer,
    MessagingMetrics metrics,
    ILogger<TaskAiReviewConsumer> logger) : IntegrationEventConsumer(inbox, metrics, logger)
{
    /// <summary>Subscription and queue name for this consumer.</summary>
    public const string Name = "ai-review";

    /// <inheritdoc />
    public override string ConsumerName => Name;

    /// <inheritdoc />
    public override bool Handles(string eventType) => eventType == nameof(TaskItemCreatedEvent);

    /// <inheritdoc />
    protected override Task ConsumeAsync(IntegrationEventEnvelope envelope, CancellationToken ct) =>
        reviewer.ReviewNewTaskAsync(
            PayloadGuid(envelope, nameof(TaskItemCreatedEvent.TaskItemId)), envelope.TenantId, ct);
}

/// <summary>Starts the ai-task-triage workflow for a newly created task.</summary>
public sealed class TaskWorkflowConsumer(
    IInboxStore inbox,
    IWorkflowTrigger workflowTrigger,
    MessagingMetrics metrics,
    ILogger<TaskWorkflowConsumer> logger) : IntegrationEventConsumer(inbox, metrics, logger)
{
    /// <summary>Subscription and queue name for this consumer.</summary>
    public const string Name = "workflow";

    /// <inheritdoc />
    public override string ConsumerName => Name;

    /// <inheritdoc />
    public override bool Handles(string eventType) => eventType == nameof(TaskItemCreatedEvent);

    /// <inheritdoc />
    protected override Task ConsumeAsync(IntegrationEventEnvelope envelope, CancellationToken ct)
    {
        var created = envelope.Payload.Deserialize<TaskItemCreatedEvent>()
            ?? throw new InvalidOperationException($"Envelope {envelope.Id} carries an empty {envelope.Type} payload.");

        // Second guard behind the inbox: FlowEngine also rejects a duplicate start on its own key.
        return workflowTrigger.OnTaskItemCreatedAsync(created, $"{envelope.Type}:{envelope.Id}", ct);
    }
}
