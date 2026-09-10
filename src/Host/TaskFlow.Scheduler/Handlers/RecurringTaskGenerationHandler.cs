using EF.Common;
using System.Globalization;
using TaskFlow.Application.Contracts.Messaging;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Model.ValueObjects;
using TaskFlow.Domain.Shared;
using TaskFlow.Domain.Shared.Constants;
using TaskFlow.Domain.Shared.Events;
using TaskFlow.Observability.Meters;
using TaskFlow.Scheduler.Abstractions;

namespace TaskFlow.Scheduler.Handlers;

/// <summary>
/// Materializes the due occurrences of recurring templates. Per template, one transaction: advance the
/// template's next-occurrence pointer under a guard, upsert the occurrences, stage a created event for each.
/// Everything is keyed deterministically, so a re-run - or two replicas racing - produces the same rows, not
/// a second set.
/// </summary>
public sealed class RecurringTaskGenerationHandler(
    ITaskItemSystemRepository systemRepository,
    IOutboxStaging outbox,
    SchedulerJobMeter meter,
    TimeProvider timeProvider,
    ILogger<RecurringTaskGenerationHandler> logger) : IScheduledJobHandler
{
    public const string JobName = "RecurringTaskGeneration";

    /// <summary>Templates read per keyset page.</summary>
    private const int PageSize = 100;

    /// <summary>Handles recurring task generation requests and returns the application result.</summary>
    public async Task HandleAsync(CancellationToken ct)
    {
        var asOfUtc = timeProvider.GetUtcNow();
        var scanned = 0;
        var generated = 0;

        await foreach (var template in systemRepository.StreamDueTemplatesAsync(asOfUtc, PageSize, ct))
        {
            scanned++;
            generated += await GenerateAsync(template, asOfUtc, ct);
        }

        meter.RecordWork(JobName, scanned, generated);
        logger.RecurringTemplatesFound(scanned);
    }

    /// <summary>Expands and persists one template's due occurrences.</summary>
    private async Task<int> GenerateAsync(TaskItem template, DateTimeOffset asOfUtc, CancellationToken ct)
    {
        var tenantId = template.TenantId.Value;
        var templateId = template.Id.Value;
        var pattern = template.RecurrencePattern;
        var dueFrom = template.NextOccurrenceAtUtc!.Value;

        if (pattern is null || !RecurrencePattern.IsSupportedFrequency(pattern.Frequency))
        {
            // Not a failure of this run: the template's data cannot be advanced. Clearing the pointer takes it
            // out of the due set so the job does not re-read it every six hours; re-saving the pattern restores it.
            logger.RecurrenceTemplateUnusable(templateId, pattern?.Frequency ?? "(none)");
            await systemRepository.AdvanceNextOccurrenceAsync(tenantId, templateId, dueFrom, null, ct);
            return 0;
        }

        var occurrenceTimes = pattern.Expand(dueFrom, asOfUtc, RecurrencePattern.MaxOccurrencesPerRun);
        var nextDue = occurrenceTimes.Count == 0 ? null : pattern.Next(occurrenceTimes[^1]);

        var occurrences = new List<TaskItem>(occurrenceTimes.Count);
        foreach (var occurrenceUtc in occurrenceTimes)
        {
            var occurrenceId = OccurrenceId(tenantId, templateId, occurrenceUtc);
            var created = TaskItem.CreateOccurrence(
                template.TenantId,
                DomainId.From<TaskItemId>(occurrenceId),
                template.Id,
                occurrenceUtc,
                template.Title,
                template.Description,
                template.Priority,
                template.CategoryId);

            if (created.IsFailure)
            {
                logger.RecurrenceOccurrenceRejected(templateId, occurrenceUtc, created.ErrorMessage ?? "unknown");
                continue;
            }

            occurrences.Add(created.Value!);
        }

        var written = 0;
        await systemRepository.ExecuteInTransactionAsync(async token =>
        {
            // The guarded advance runs first and is the lock: if another replica already moved this template,
            // its occurrences are the same rows this run would write, so this transaction commits nothing.
            if (!await systemRepository.AdvanceNextOccurrenceAsync(tenantId, templateId, dueFrom, nextDue, token))
                return;

            if (occurrences.Count == 0) return;

            await systemRepository.UpsertOccurrencesAsync(occurrences, token);
            foreach (var occurrence in occurrences) Stage(occurrence, asOfUtc);
            await systemRepository.SaveChangesAsync(token);
            written = occurrences.Count;
        }, ct);

        return written;
    }

    /// <summary>
    /// Stages the created event with the occurrence's own id as the message id. The occurrence is written by an
    /// upsert, which never runs the domain-event interceptor, so the event is staged here explicitly.
    /// </summary>
    private void Stage(TaskItem occurrence, DateTimeOffset asOfUtc)
    {
        var messageId = occurrence.Id.Value;
        var envelope = TaskFlowIntegrationEvents.Envelope(
            new TaskItemCreatedEvent(messageId, occurrence.TenantId.Value, occurrence.Title),
            asOfUtc,
            correlationId: null,
            id: messageId);

        outbox.Stage(envelope, occurrence.TenantId.Value, messageId);
    }

    /// <summary>UUIDv5 over (tenant, template, occurrence): the same occurrence always gets the same id.</summary>
    private static Guid OccurrenceId(Guid tenantId, Guid templateId, DateTimeOffset occurrenceUtc) =>
        DeterministicGuid.Create(
            DomainConstants.DETERMINISTIC_ID_NAMESPACE,
            "recurrence",
            tenantId.ToString(),
            templateId.ToString(),
            occurrenceUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
}
