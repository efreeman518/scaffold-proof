using EF.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.Contracts.Services;
using TaskFlow.Domain.Shared;
using TaskFlow.Domain.Shared.Events;

namespace TaskFlow.Application.MessageHandlers.Consumers;

// D-034: the consumers behind both transports derive from EF.Messaging's IntegrationEventConsumerBase, so the
// Azure Functions Service Bus triggers and the RabbitMQ handlers run the same D-029 two-state inbox: a claim with
// a 60 s lease renewed by the owning delivery, a bounded wait (lease + 5 s) on a live foreign claim, completion
// only after the effect ran, release on failure. Malformed and unsupported envelopes are rejected by the
// transport-side reader before a consumer is reached.

/// <summary>Reads the owning tenant from an envelope payload.</summary>
internal static class PayloadTenant
{
    /// <summary>
    /// Owning tenant of the message. The package envelope frame carries no tenant, so it is read from the
    /// payload, which every <see cref="ITenantDomainEvent"/> carries and which the producer copied onto the
    /// broker <c>TenantId</c> header.
    /// </summary>
    /// <exception cref="InvalidOperationException">The payload has no Guid TenantId.</exception>
    public static Guid Of(IntegrationEventEnvelope envelope) =>
        envelope.Payload.TryGetProperty(nameof(ITenantDomainEvent.TenantId), out var value) && value.TryGetGuid(out var id)
            ? id
            : throw new InvalidOperationException(
                $"Envelope {envelope.Id} ({envelope.Type}) has no {nameof(ITenantDomainEvent.TenantId)}.");
}

/// <summary>
/// Rebuilds the Cosmos TaskView read model. A full re-projection runs only on create and status change; the
/// delta events adjust counters in place so a busy task does not re-read its whole graph per comment.
/// </summary>
public sealed class TaskProjectionConsumer(
    IInboxStore inbox,
    ITaskViewProjectionService projection,
    MessagingMetrics metrics,
    ILogger<TaskProjectionConsumer> logger,
    IOptions<InboxClaimOptions>? claimOptions = null) : IntegrationEventConsumerBase(inbox, metrics, logger, claimOptions)
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
            PayloadGuid(envelope, nameof(CommentAddedEvent.TaskItemId)), PayloadTenant.Of(envelope),
            new TaskViewCounterDelta(CommentCount: 1), envelope.OccurredAtUtc, ct),
        nameof(AttachmentUploadedEvent) => projection.AdjustCountersAsync(
            PayloadGuid(envelope, nameof(AttachmentUploadedEvent.OwnerId)), PayloadTenant.Of(envelope),
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
    ILogger<TaskEmbeddingConsumer> logger,
    IOptions<InboxClaimOptions>? claimOptions = null) : IntegrationEventConsumerBase(inbox, metrics, logger, claimOptions)
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
        var tenantId = PayloadTenant.Of(envelope);

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
    ILogger<TaskAiReviewConsumer> logger,
    IOptions<InboxClaimOptions>? claimOptions = null) : IntegrationEventConsumerBase(inbox, metrics, logger, claimOptions)
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
            PayloadGuid(envelope, nameof(TaskItemCreatedEvent.TaskItemId)), PayloadTenant.Of(envelope), ct);
}

/// <summary>Starts the ai-task-triage workflow for a newly created task.</summary>
public sealed class TaskWorkflowConsumer(
    IInboxStore inbox,
    IWorkflowTrigger workflowTrigger,
    MessagingMetrics metrics,
    ILogger<TaskWorkflowConsumer> logger,
    IOptions<InboxClaimOptions>? claimOptions = null) : IntegrationEventConsumerBase(inbox, metrics, logger, claimOptions)
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
