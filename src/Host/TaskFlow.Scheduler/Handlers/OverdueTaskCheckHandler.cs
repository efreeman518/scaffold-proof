using EF.Common;
using TaskFlow.Application.Contracts.Messaging;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Domain.Shared;
using TaskFlow.Domain.Shared.Constants;
using TaskFlow.Domain.Shared.Events;
using TaskFlow.Observability.Meters;
using TaskFlow.Scheduler.Abstractions;
using System.Globalization;

namespace TaskFlow.Scheduler.Handlers;

/// <summary>
/// Announces tasks that have passed their due date. Cross-tenant by construction: it streams candidates over
/// the system repository, and per tenant marks them and stages one <c>TaskItemOverdueSuspectedEvent</c> in a
/// single transaction. The marker is the due date itself, so re-running finds nothing but rescheduling a task
/// makes it a candidate again.
/// </summary>
public sealed class OverdueTaskCheckHandler(
    ITaskItemSystemRepository systemRepository,
    IOutboxStaging outbox,
    SchedulerJobMeter meter,
    TimeProvider timeProvider,
    ILogger<OverdueTaskCheckHandler> logger) : IScheduledJobHandler
{
    public const string JobName = "OverdueTaskCheck";

    /// <summary>Rows read per keyset page, and the size of one tenant transaction at most.</summary>
    private const int PageSize = 200;

    /// <summary>Handles overdue task check requests and returns the application result.</summary>
    public async Task HandleAsync(CancellationToken ct)
    {
        var asOfUtc = timeProvider.GetUtcNow();
        var scanned = 0;
        var notified = 0;
        var page = new List<OverdueTaskRow>(PageSize);

        await foreach (var row in systemRepository.StreamOverdueAsync(asOfUtc, PageSize, ct))
        {
            page.Add(row);
            scanned++;
            if (page.Count < PageSize) continue;

            notified += await FlushAsync(page, asOfUtc, ct);
            page.Clear();
        }

        if (page.Count > 0) notified += await FlushAsync(page, asOfUtc, ct);

        meter.RecordWork(JobName, scanned, notified);
        logger.OverdueTasksFound(notified);
    }

    /// <summary>Marks and announces one page, one transaction per tenant.</summary>
    private async Task<int> FlushAsync(List<OverdueTaskRow> page, DateTimeOffset asOfUtc, CancellationToken ct)
    {
        var notified = 0;

        foreach (var tenant in page.GroupBy(r => r.TenantId))
        {
            var rows = tenant.ToList();
            await systemRepository.ExecuteInTransactionAsync(async token =>
            {
                var marked = await systemRepository.MarkOverdueNotifiedAsync(
                    tenant.Key, rows.ConvertAll(r => r.Id), asOfUtc, token);
                // Every row lost the race (completed or rescheduled since the scan): nothing to announce.
                if (marked == 0) return;

                foreach (var row in rows) Stage(row, asOfUtc);
                await systemRepository.SaveChangesAsync(token);
                notified += marked;
            }, ct);
        }

        return notified;
    }

    /// <summary>
    /// Stages the announcement with a UUIDv5 message id over (tenant, task, due date). Two replicas that both
    /// reach this point stage the same row rather than two, and the event is "suspected" precisely because the
    /// consumer, not this job, confirms the task is still overdue when it handles the message.
    /// </summary>
    private void Stage(OverdueTaskRow row, DateTimeOffset asOfUtc)
    {
        var messageId = DeterministicGuid.Create(
            DomainConstants.DETERMINISTIC_ID_NAMESPACE,
            "overdue",
            row.TenantId.ToString(),
            row.Id.ToString(),
            row.DueDate.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

        var envelope = IntegrationEventEnvelope.From(
            new TaskItemOverdueSuspectedEvent(row.Id, row.TenantId, row.DueDate),
            asOfUtc,
            correlationId: null,
            id: messageId);

        outbox.Stage(envelope, messageId);
    }
}
