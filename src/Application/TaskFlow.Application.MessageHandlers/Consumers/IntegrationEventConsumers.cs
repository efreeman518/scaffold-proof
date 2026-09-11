using EF.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;
using TaskFlow.Application.Contracts.Messaging;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.Contracts.Services;
using TaskFlow.Domain.Shared;
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

    /// <summary>
    /// Owning tenant of the message. The package envelope frame carries no tenant, so it is read from the
    /// payload, which every <see cref="IDomainEvent"/> carries and which is the value the producer denormalized
    /// onto the outbox row and the broker message property.
    /// </summary>
    protected static Guid PayloadTenant(IntegrationEventEnvelope envelope) =>
        PayloadGuid(envelope, nameof(IDomainEvent.TenantId));
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
            PayloadGuid(envelope, nameof(CommentAddedEvent.TaskItemId)), PayloadTenant(envelope),
            new TaskViewCounterDelta(CommentCount: 1), envelope.OccurredAtUtc, ct),
        nameof(AttachmentUploadedEvent) => projection.AdjustCountersAsync(
            PayloadGuid(envelope, nameof(AttachmentUploadedEvent.OwnerId)), PayloadTenant(envelope),
            new TaskViewCounterDelta(AttachmentCount: 1), envelope.OccurredAtUtc, ct),
        _ => projection.ProjectTaskItemAsync(
            PayloadGuid(envelope, nameof(TaskItemCreatedEvent.TaskItemId)), envelope.OccurredAtUtc, ct)
    };
}

/// <summary>
/// Maintains the pgvector search projection (D-040): re-embeds a task's title and description whenever the
/// task is created or its text changes, and removes the row when the task is gone. Registered only when
/// <c>Search:Provider</c> resolves to PgVector, and its queue and subscription are declared only then too,
/// so on any other arm there is nothing to accumulate.
/// </summary>
public sealed class TaskEmbeddingConsumer(
    IInboxStore inbox,
    ITaskEmbeddingRepository embeddings,
    IEmbeddingGenerator<string, Embedding<float>> generator,
    MessagingMetrics metrics,
    ILogger<TaskEmbeddingConsumer> logger) : IntegrationEventConsumer(inbox, metrics, logger)
{
    /// <summary>Subscription and queue name for this consumer.</summary>
    public const string Name = "embedding";

    /// <inheritdoc />
    public override string ConsumerName => Name;

    /// <inheritdoc />
    // Only the two events that change embeddable text. A status or completion event never touches Title or
    // Description, so binding them would pay for a model call to write back an identical vector.
    public override bool Handles(string eventType) => eventType is
        nameof(TaskItemCreatedEvent) or nameof(TaskItemContentChangedEvent);

    /// <inheritdoc />
    protected override async Task ConsumeAsync(IntegrationEventEnvelope envelope, CancellationToken ct)
    {
        var taskItemId = PayloadGuid(envelope, nameof(TaskItemCreatedEvent.TaskItemId));
        var tenantId = PayloadTenant(envelope);

        var source = await embeddings.GetSourceAsync(tenantId, taskItemId, ct).ConfigureAwait(false);
        if (source is null)
        {
            // Event delivery is asynchronous and races deletion. Deleting rather than skipping is what keeps
            // a deleted task from staying searchable; a missing row makes the delete a no-op.
            await embeddings.DeleteAsync(tenantId, taskItemId, ct).ConfigureAwait(false);
            logger.TaskEmbeddingRemovedForMissingTask(taskItemId, tenantId);
            return;
        }

        var text = string.IsNullOrWhiteSpace(source.Description)
            ? source.Title
            : $"{source.Title}\n\n{source.Description}";

        var embedding = await generator.GenerateAsync(text, cancellationToken: ct).ConfigureAwait(false);

        // The model that produced the vector is recorded with it: vectors from two models are not comparable,
        // so a model change has to be visible in the data, not only in configuration.
        var modelId = embedding.ModelId ?? generator.GetService<EmbeddingGeneratorMetadata>()?.DefaultModelId ?? "unknown";

        await embeddings.UpsertAsync(
            tenantId, taskItemId, embedding.Vector, modelId, envelope.OccurredAtUtc, ct).ConfigureAwait(false);
        logger.TaskEmbeddingUpserted(taskItemId, modelId, embedding.Vector.Length);
    }
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
            PayloadGuid(envelope, nameof(TaskItemCreatedEvent.TaskItemId)), PayloadTenant(envelope), ct);
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
