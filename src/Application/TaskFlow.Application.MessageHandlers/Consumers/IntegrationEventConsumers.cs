using EF.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Text.Json;
using TaskFlow.Application.Contracts.Messaging;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.Contracts.Services;
using TaskFlow.Domain.Shared;
using TaskFlow.Domain.Shared.Events;
using TaskFlow.Observability.Meters;
using IInboxStore = TaskFlow.Application.Contracts.Messaging.IInboxStore;
using InboxClaim = TaskFlow.Application.Contracts.Messaging.InboxClaim;
using InboxClaimStatus = TaskFlow.Application.Contracts.Messaging.InboxClaimStatus;
using MessagingMetrics = TaskFlow.Observability.Meters.MessagingMetrics;

namespace TaskFlow.Application.MessageHandlers.Consumers;

/// <summary>
/// Shared consumer logic behind both transports (D-034): the Azure Functions Service Bus triggers and the
/// RabbitMQ handler wrappers call these, so provider choice never changes behavior. Malformed and unsupported
/// envelopes are rejected by the transport-side reader before a consumer is reached; a consumer that throws is
/// a transient failure and the transport redelivers.
/// </summary>
/// <remarks>
/// Idempotency is the D-029 two-state inbox, tuned for RabbitMQ first (immediate redelivery, immediate requeue of
/// a thrown delivery) and Service Bus second:
/// <list type="number">
/// <item>Claim with a short lease (<see cref="InboxClaimOptions.ClaimLease"/>), renewed every third of it while
/// the handler runs, so a live holder keeps its claim however long it works and a crashed one loses it within
/// one lease.</item>
/// <item>A delivery that meets a live foreign claim waits for it, polling: completed means duplicate (acknowledge
/// without running), expired means take it over and run. Only a claim still live after one lease plus a margin
/// throws <see cref="InboxClaimInProgressException"/> for a transport retry. Throwing at once instead would burn
/// a RabbitMQ message's whole delivery budget in milliseconds after a consumer crash and dead-letter it.</item>
/// <item>Complete only after the effect ran; a failed effect releases the claim. Both are guarded by the claim
/// token and ignore the delivery token.</item>
/// </list>
/// A duplicate-PK rollback would have to read provider-specific exception shapes (SqlException 2627 vs
/// PostgresException 23505), which D-030 rules out.
/// </remarks>
public abstract class IntegrationEventConsumer
{
    private readonly IInboxStore _inbox;
    private readonly MessagingMetrics _metrics;
    private readonly ILogger _logger;
    private readonly InboxClaimOptions _claim;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the consumer base.</summary>
    /// <param name="inbox">Two-state inbox.</param>
    /// <param name="metrics">Messaging metrics.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="claimOptions">Claim timings; defaults when null.</param>
    /// <param name="timeProvider">Clock for the lease, the renewal period and the wait; tests supply a fake.</param>
    protected IntegrationEventConsumer(
        IInboxStore inbox,
        MessagingMetrics metrics,
        ILogger logger,
        IOptions<InboxClaimOptions>? claimOptions = null,
        TimeProvider? timeProvider = null)
    {
        _inbox = inbox;
        _metrics = metrics;
        _logger = logger;
        _claim = claimOptions?.Value ?? new InboxClaimOptions();
        if (!_claim.IsValid())
            throw new ArgumentException("Inbox claim timings must be positive with PollInterval below ClaimLease.", nameof(claimOptions));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Inbox consumer name; also the Service Bus subscription and RabbitMQ queue suffix.</summary>
    public abstract string ConsumerName { get; }

    /// <summary>True when this consumer acts on the envelope's event type at all.</summary>
    public abstract bool Handles(string eventType);

    /// <summary>Runs the consumer's own work for an envelope it has claimed.</summary>
    protected abstract Task ConsumeAsync(IntegrationEventEnvelope envelope, CancellationToken ct);

    /// <summary>
    /// Claims (waiting out a live foreign claim for up to one lease), consumes while renewing the claim, then
    /// completes it. A duplicate returns without running; a claim still held by another live delivery at the end
    /// of the wait throws <see cref="InboxClaimInProgressException"/> so the transport retries; a failed consume
    /// releases the claim and rethrows the original exception.
    /// </summary>
    public async Task HandleAsync(IntegrationEventEnvelope envelope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!Handles(envelope.Type)) return;

        var claim = await ClaimAsync(envelope, ct).ConfigureAwait(false);
        if (claim.Status == InboxClaimStatus.Duplicate)
        {
            _metrics.RecordInboxDuplicate(ConsumerName);
            _logger.ConsumerDuplicateSkipped(ConsumerName, envelope.Type, envelope.Id);
            return;
        }

        var started = Stopwatch.GetTimestamp();
        using var renewalStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var renewal = RenewWhileRunningAsync(envelope, claim.ClaimToken, renewalStop.Token);
        try
        {
            await ConsumeAsync(envelope, ct).ConfigureAwait(false);
        }
        catch (Exception consumeFailure)
        {
            await StopRenewalAsync(renewalStop, renewal).ConfigureAwait(false);
            await ReleaseAfterFailureAsync(envelope, claim.ClaimToken, consumeFailure).ConfigureAwait(false);
            throw;
        }

        // Renewal stops before the claim is settled, so it can never extend a claim that is being completed.
        await StopRenewalAsync(renewalStop, renewal).ConfigureAwait(false);

        // Not the delivery token: the effect has run, and a shutdown now must not leave the claim in progress,
        // which would hold every redelivery back until the lease expired.
        if (!await _inbox.CompleteAsync(ConsumerName, envelope.Id, claim.ClaimToken, CancellationToken.None)
                .ConfigureAwait(false))
        {
            _logger.ConsumerClaimLost(ConsumerName, envelope.Type, envelope.Id);
        }

        _metrics.RecordConsumerDuration(ConsumerName, Stopwatch.GetElapsedTime(started));
    }

    /// <summary>
    /// Takes the claim, or waits for a live foreign claim to resolve: re-reads it every
    /// <see cref="InboxClaimOptions.PollInterval"/> until it is completed (duplicate) or expired (taken over),
    /// for at most <see cref="InboxClaimOptions.WaitBound"/> and never past the delivery token.
    /// </summary>
    private async Task<InboxClaim> ClaimAsync(IntegrationEventEnvelope envelope, CancellationToken ct)
    {
        var deadline = _timeProvider.GetUtcNow() + _claim.WaitBound;
        while (true)
        {
            var claim = await _inbox.TryClaimAsync(ConsumerName, envelope.Id, _claim.ClaimLease, ct).ConfigureAwait(false);
            if (claim.Status != InboxClaimStatus.InProgress) return claim;

            if (_timeProvider.GetUtcNow() >= deadline)
            {
                // Still live after a whole lease plus margin: the holder is alive and renewing. The transport
                // retries this delivery; by then the holder has completed (duplicate) or failed (released).
                _metrics.RecordInboxInProgress(ConsumerName);
                _logger.ConsumerClaimInProgress(ConsumerName, envelope.Type, envelope.Id);
                throw new InboxClaimInProgressException(ConsumerName, envelope.Id);
            }

            await Task.Delay(_claim.PollInterval, _timeProvider, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Renews the claim every <see cref="InboxClaimOptions.RenewalInterval"/> until stopped. A failed renewal is
    /// logged and the handler keeps going - the next renewal may succeed, and the lease still has two thirds
    /// left; a renewal that finds the claim gone (taken over) stops renewing and warns. It never faults.
    /// </summary>
    private async Task RenewWhileRunningAsync(IntegrationEventEnvelope envelope, Guid claimToken, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(_claim.RenewalInterval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                try
                {
                    if (!await _inbox.RenewAsync(ConsumerName, envelope.Id, claimToken, _claim.ClaimLease, stop)
                            .ConfigureAwait(false))
                    {
                        _logger.ConsumerClaimRenewalLost(ConsumerName, envelope.Type, envelope.Id);
                        return;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested)
                {
                    _logger.ConsumerClaimRenewalFailed(ex, ConsumerName, envelope.Type, envelope.Id);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Stopped: the handler finished, or the delivery was cancelled.
        }
    }

    private static async Task StopRenewalAsync(CancellationTokenSource renewalStop, Task renewal)
    {
        await renewalStop.CancelAsync().ConfigureAwait(false);
        await renewal.ConfigureAwait(false);
    }

    /// <summary>
    /// Releases the claim of a failed consume so the redelivery runs immediately. A release failure (typically the
    /// same outage that failed the work) is logged against the original failure and does not replace it: the
    /// caller rethrows the original, and the claim's lease expiry lets the redelivery take it over.
    /// </summary>
    private async Task ReleaseAfterFailureAsync(IntegrationEventEnvelope envelope, Guid claimToken, Exception consumeFailure)
    {
        try
        {
            await _inbox.ReleaseAsync(ConsumerName, envelope.Id, claimToken, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception releaseFailure)
        {
            _logger.ConsumerReleaseFailed(
                new AggregateException(consumeFailure, releaseFailure), ConsumerName, envelope.Type, envelope.Id);
        }
    }

    /// <summary>Reads a required Guid property from the envelope payload.</summary>
    protected static Guid PayloadGuid(IntegrationEventEnvelope envelope, string property) =>
        envelope.Payload.TryGetProperty(property, out var value) && value.TryGetGuid(out var id)
            ? id
            : throw new InvalidOperationException($"Envelope {envelope.Id} ({envelope.Type}) has no {property}.");

    /// <summary>
    /// Owning tenant of the message. The package envelope frame carries no tenant, so it is read from the
    /// payload, which every <see cref="ITenantDomainEvent"/> carries and which is the value the producer denormalized
    /// onto the outbox row and the broker message property.
    /// </summary>
    protected static Guid PayloadTenant(IntegrationEventEnvelope envelope) =>
        PayloadGuid(envelope, nameof(ITenantDomainEvent.TenantId));
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
    IOptions<InboxClaimOptions>? claimOptions = null) : IntegrationEventConsumer(inbox, metrics, logger, claimOptions)
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
    ILogger<TaskEmbeddingConsumer> logger,
    IOptions<InboxClaimOptions>? claimOptions = null) : IntegrationEventConsumer(inbox, metrics, logger, claimOptions)
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
    ILogger<TaskAiReviewConsumer> logger,
    IOptions<InboxClaimOptions>? claimOptions = null) : IntegrationEventConsumer(inbox, metrics, logger, claimOptions)
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
    ILogger<TaskWorkflowConsumer> logger,
    IOptions<InboxClaimOptions>? claimOptions = null) : IntegrationEventConsumer(inbox, metrics, logger, claimOptions)
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
