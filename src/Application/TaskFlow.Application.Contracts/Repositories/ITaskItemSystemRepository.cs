using TaskFlow.Domain.Model;

namespace TaskFlow.Application.Contracts.Repositories;

/// <summary>One overdue candidate: its keyset position plus the due date the notification is keyed on.</summary>
public readonly record struct OverdueTaskRow(Guid TenantId, Guid Id, DateTimeOffset DueDate);

/// <summary>Keyset position of one stale (cancelled, past retention) task.</summary>
public readonly record struct StaleTaskRow(Guid TenantId, Guid Id);

/// <summary>
/// Cross-tenant system access for the scheduler jobs. Every query runs with <c>IgnoreQueryFilters()</c> on the
/// write context, so the jobs no longer depend on the background request context defaulting to global admin -
/// a default that silently granted every background component tenant-wide reach.
/// <para>
/// Batch methods take <c>(tenantId, ids)</c> rather than composite keys because EF cannot translate a tuple
/// <c>IN</c>; callers group each keyset page by tenant before calling them.
/// </para>
/// </summary>
public interface ITaskItemSystemRepository
{
    /// <summary>
    /// Streams tasks that are past due and have not been notified for that due date, keyset-paged by
    /// <c>(TenantId, Id)</c>. Changing a task's due date makes it a candidate again, which is the point:
    /// the marker records which due date was announced, not merely that something was.
    /// </summary>
    IAsyncEnumerable<OverdueTaskRow> StreamOverdueAsync(DateTimeOffset asOfUtc, int pageSize, CancellationToken ct = default);

    /// <summary>
    /// Stamps <c>OverdueNotifiedForDueDate</c> on one task, re-asserting the candidate predicate in the WHERE so a
    /// task that changed between the scan and the update is not falsely marked. One row per call, so the caller
    /// knows exactly which rows it marked (and so must announce) when another replica marked the rest.
    /// </summary>
    /// <returns><c>true</c> when this call marked the task.</returns>
    Task<bool> MarkOverdueNotifiedAsync(Guid tenantId, Guid id, DateTimeOffset asOfUtc, CancellationToken ct = default);

    /// <summary>
    /// Streams recurrence templates whose next occurrence is due, keyset-paged by <c>(TenantId, Id)</c>.
    /// Entities rather than a projection: the generator needs the owned JSON recurrence pattern and the
    /// scalar fields the occurrence is copied from, and the due set is small by construction.
    /// </summary>
    IAsyncEnumerable<TaskItem> StreamDueTemplatesAsync(DateTimeOffset asOfUtc, int pageSize, CancellationToken ct = default);

    /// <summary>
    /// Inserts one generated occurrence if absent, keyed on <c>(TenantId, RecurrenceTemplateId, OccurrenceUtc)</c>
    /// (D-028: MERGE on SQL Server, ON CONFLICT on PostgreSQL). A replayed run is a no-op, not a duplicate. One row
    /// per call, so the caller knows exactly which occurrences it inserted (and so must announce).
    /// </summary>
    /// <returns><c>true</c> when this call inserted the occurrence.</returns>
    Task<bool> InsertOccurrenceIfAbsentAsync(TaskItem occurrence, CancellationToken ct = default);

    /// <summary>
    /// Moves a template forward only while it still sits on <paramref name="expectedNextUtc"/>. False means
    /// another replica already advanced it, so this run's occurrences were duplicates and its transaction
    /// should roll back rather than double-advance the series.
    /// </summary>
    Task<bool> AdvanceNextOccurrenceAsync(
        Guid tenantId, Guid templateId, DateTimeOffset expectedNextUtc, DateTimeOffset? newNextUtc, CancellationToken ct = default);

    /// <summary>
    /// One keyset page of cancelled tasks whose terminal timestamp is older than <paramref name="cutoffUtc"/>.
    /// Tasks that still have subtasks are skipped: the subtask FK is Restrict, and they become deletable
    /// once their children are gone.
    /// </summary>
    Task<IReadOnlyList<StaleTaskRow>> GetStaleBatchAsync(
        DateTimeOffset cutoffUtc, StaleTaskRow? after, int pageSize, CancellationToken ct = default);

    /// <summary>
    /// Records a deferred blob deletion for every attachment of the given tasks and commits those rows.
    /// The blob itself is deleted by the work drainer, so a storage outage cannot block the row deletion.
    /// Pass only tasks this step's own <see cref="DeleteStaleTaskAsync"/> removed: the work ids are deterministic,
    /// so a run that queued work for a task another run removed would collide with that run's rows.
    /// </summary>
    Task<int> StageBlobDeletesAsync(Guid tenantId, IReadOnlyCollection<Guid> taskIds, CancellationToken ct = default);

    /// <summary>
    /// Deletes one task, re-asserting the stale predicate so a task that was reopened (or gained a subtask, or was
    /// removed by another run) between scan and delete is left alone. Comments, checklist items, and tag links
    /// cascade; attachments do not, see <see cref="DeleteAttachmentsAsync"/>.
    /// </summary>
    /// <returns><c>true</c> when this call deleted the task.</returns>
    Task<bool> DeleteStaleTaskAsync(Guid tenantId, Guid taskId, DateTimeOffset cutoffUtc, CancellationToken ct = default);

    /// <summary>
    /// Deletes the attachment rows of the given tasks. Attachments hang off a polymorphic owner, not a foreign key,
    /// so nothing cascades them; pass only tasks <see cref="DeleteStaleTaskAsync"/> removed.
    /// </summary>
    Task<int> DeleteAttachmentsAsync(Guid tenantId, IReadOnlyCollection<Guid> taskIds, CancellationToken ct = default);

    /// <summary>
    /// Runs <paramref name="work"/> inside one transaction on the write context, through the provider's
    /// execution strategy. Both providers retry on transient failures and reject a user transaction opened
    /// outside the strategy, so every multi-statement job step goes through here.
    /// <para>
    /// The strategy may run <paramref name="work"/> more than once, including after a commit that landed but
    /// reported a failure. Each attempt starts from a cleared change tracker, so <paramref name="work"/> re-reads
    /// and re-guards what it changes and re-stages its rows, and its guards must make a re-run after a landed
    /// commit a no-op. The caller sees only the committed attempt's result, so counts and other side effects
    /// belong after this call, never inside <paramref name="work"/>.
    /// </para>
    /// </summary>
    /// <returns>What the committed attempt of <paramref name="work"/> returned.</returns>
    /// <exception cref="InvalidOperationException">The write context has pending changes when this is called.</exception>
    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default);

    /// <summary>
    /// Commits rows staged on the shared write context (outbox rows, blob-delete work) with
    /// <c>OptimisticConcurrencyWinner.Throw</c> (D-032). Named for the policy rather than <c>SaveChangesAsync</c> so the
    /// architecture rule, which flags every <c>SaveChangesAsync</c> call that is not Throw, never has to trust an app contract.
    /// </summary>
    Task<int> SaveWithThrowPolicyAsync(CancellationToken ct = default);
}
