using EF.Messaging.Outbox;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Domain.Model;

namespace Test.Unit.Services;

/// <summary>
/// In-memory stand-in for the scheduler's system repository. Records every call in order so the handler tests
/// can assert the sequence the jobs depend on (guard before write, stage before delete) rather than only the
/// final state - the ordering is the part that makes a re-run idempotent.
/// </summary>
internal sealed class FakeTaskItemSystemRepository : ITaskItemSystemRepository
{
    public List<OverdueTaskRow> OverdueRows { get; } = [];
    public List<TaskItem> DueTemplates { get; } = [];
    public Queue<IReadOnlyList<StaleTaskRow>> StaleBatches { get; } = new();

    /// <summary>Task ids the guarded overdue mark rejects (lost the race); every other id is marked.</summary>
    public HashSet<Guid> NotMarked { get; } = [];

    /// <summary>Task ids the guarded stale delete finds gone or no longer stale; every other id is deleted.</summary>
    public HashSet<Guid> NotDeleted { get; } = [];

    /// <summary>Occurrence ids already stored, so the insert-if-absent writes nothing for them.</summary>
    public HashSet<Guid> ExistingOccurrences { get; } = [];

    /// <summary>Result the guarded next-occurrence advance returns.</summary>
    public bool AdvanceResult { get; set; } = true;

    public List<string> Calls { get; } = [];
    public List<(Guid TenantId, Guid Id)> MarkedOverdue { get; } = [];
    public List<TaskItem> UpsertedOccurrences { get; } = [];
    public List<(Guid TenantId, Guid TemplateId, DateTimeOffset Guard, DateTimeOffset? Next)> Advances { get; } = [];
    public List<(Guid TenantId, IReadOnlyCollection<Guid> Ids)> StagedBlobDeletes { get; } = [];
    public List<(Guid TenantId, Guid Id, DateTimeOffset Cutoff)> Deletes { get; } = [];
    public int SaveCount { get; private set; }
    public int TransactionCount { get; private set; }

    public async IAsyncEnumerable<OverdueTaskRow> StreamOverdueAsync(
        DateTimeOffset asOfUtc, int pageSize,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        Calls.Add(nameof(StreamOverdueAsync));
        foreach (var row in OverdueRows)
        {
            await Task.Yield();
            yield return row;
        }
    }

    public Task<bool> MarkOverdueNotifiedAsync(Guid tenantId, Guid id, DateTimeOffset asOfUtc, CancellationToken ct = default)
    {
        Calls.Add(nameof(MarkOverdueNotifiedAsync));
        MarkedOverdue.Add((tenantId, id));
        return Task.FromResult(!NotMarked.Contains(id));
    }

    public async IAsyncEnumerable<TaskItem> StreamDueTemplatesAsync(
        DateTimeOffset asOfUtc, int pageSize,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        Calls.Add(nameof(StreamDueTemplatesAsync));
        foreach (var template in DueTemplates)
        {
            await Task.Yield();
            yield return template;
        }
    }

    public Task<bool> InsertOccurrenceIfAbsentAsync(TaskItem occurrence, CancellationToken ct = default)
    {
        Calls.Add(nameof(InsertOccurrenceIfAbsentAsync));
        UpsertedOccurrences.Add(occurrence);
        return Task.FromResult(!ExistingOccurrences.Contains(occurrence.Id.Value));
    }

    public Task<bool> AdvanceNextOccurrenceAsync(
        Guid tenantId, Guid templateId, DateTimeOffset expectedNextUtc, DateTimeOffset? newNextUtc, CancellationToken ct = default)
    {
        Calls.Add(nameof(AdvanceNextOccurrenceAsync));
        Advances.Add((tenantId, templateId, expectedNextUtc, newNextUtc));
        return Task.FromResult(AdvanceResult);
    }

    public Task<IReadOnlyList<StaleTaskRow>> GetStaleBatchAsync(
        DateTimeOffset cutoffUtc, StaleTaskRow? after, int pageSize, CancellationToken ct = default)
    {
        Calls.Add(nameof(GetStaleBatchAsync));
        return Task.FromResult(StaleBatches.Count > 0 ? StaleBatches.Dequeue() : (IReadOnlyList<StaleTaskRow>)[]);
    }

    public Task<int> StageBlobDeletesAsync(Guid tenantId, IReadOnlyCollection<Guid> taskIds, CancellationToken ct = default)
    {
        Calls.Add(nameof(StageBlobDeletesAsync));
        StagedBlobDeletes.Add((tenantId, taskIds));
        return Task.FromResult(taskIds.Count);
    }

    public Task<bool> DeleteStaleTaskAsync(Guid tenantId, Guid taskId, DateTimeOffset cutoffUtc, CancellationToken ct = default)
    {
        Calls.Add(nameof(DeleteStaleTaskAsync));
        Deletes.Add((tenantId, taskId, cutoffUtc));
        return Task.FromResult(!NotDeleted.Contains(taskId));
    }

    public Task<int> DeleteAttachmentsAsync(Guid tenantId, IReadOnlyCollection<Guid> taskIds, CancellationToken ct = default)
    {
        Calls.Add(nameof(DeleteAttachmentsAsync));
        return Task.FromResult(taskIds.Count);
    }

    public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default)
    {
        TransactionCount++;
        Calls.Add("BeginTransaction");
        var result = await work(ct);
        Calls.Add("Commit");
        return result;
    }

    public Task<int> SaveWithThrowPolicyAsync(CancellationToken ct = default)
    {
        SaveCount++;
        Calls.Add(nameof(SaveWithThrowPolicyAsync));
        return Task.FromResult(0);
    }
}

/// <summary>
/// Frozen clock for the scheduler handlers. Two lines instead of the TimeProvider.Testing package: the jobs
/// only ever read <c>GetUtcNow</c>, and none of them wait on a timer.
/// </summary>
internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}

/// <summary>Captures staged outbox entries; the envelope id is the message id each was staged under.</summary>
internal sealed class FakeOutboxStaging : IOutboxStaging
{
    public List<OutboxEntry> Staged { get; } = [];

    public void Stage(OutboxEntry entry) => Staged.Add(entry);
}
