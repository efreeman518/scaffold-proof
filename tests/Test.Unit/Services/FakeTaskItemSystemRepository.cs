using EF.Messaging;
using TaskFlow.Application.Contracts.Messaging;
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

    /// <summary>Rows the guarded ExecuteUpdate reports as marked; -1 means "all requested".</summary>
    public int MarkedOverride { get; set; } = -1;

    /// <summary>Result the guarded next-occurrence advance returns.</summary>
    public bool AdvanceResult { get; set; } = true;

    public List<string> Calls { get; } = [];
    public List<(Guid TenantId, IReadOnlyCollection<Guid> Ids)> MarkedOverdue { get; } = [];
    public List<TaskItem> UpsertedOccurrences { get; } = [];
    public List<(Guid TenantId, Guid TemplateId, DateTimeOffset Guard, DateTimeOffset? Next)> Advances { get; } = [];
    public List<(Guid TenantId, IReadOnlyCollection<Guid> Ids)> StagedBlobDeletes { get; } = [];
    public List<(Guid TenantId, IReadOnlyCollection<Guid> Ids, DateTimeOffset Cutoff)> Deletes { get; } = [];
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

    public Task<int> MarkOverdueNotifiedAsync(
        Guid tenantId, IReadOnlyCollection<Guid> ids, DateTimeOffset asOfUtc, CancellationToken ct = default)
    {
        Calls.Add(nameof(MarkOverdueNotifiedAsync));
        MarkedOverdue.Add((tenantId, ids));
        return Task.FromResult(MarkedOverride < 0 ? ids.Count : MarkedOverride);
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

    public Task<int> UpsertOccurrencesAsync(IReadOnlyCollection<TaskItem> occurrences, CancellationToken ct = default)
    {
        Calls.Add(nameof(UpsertOccurrencesAsync));
        UpsertedOccurrences.AddRange(occurrences);
        return Task.FromResult(occurrences.Count);
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

    public Task<int> DeleteStaleBatchAsync(
        Guid tenantId, IReadOnlyCollection<Guid> taskIds, DateTimeOffset cutoffUtc, CancellationToken ct = default)
    {
        Calls.Add(nameof(DeleteStaleBatchAsync));
        Deletes.Add((tenantId, taskIds, cutoffUtc));
        return Task.FromResult(taskIds.Count);
    }

    public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> work, CancellationToken ct = default)
    {
        TransactionCount++;
        Calls.Add("BeginTransaction");
        await work(ct);
        Calls.Add("Commit");
    }

    public Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        SaveCount++;
        Calls.Add(nameof(SaveChangesAsync));
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

/// <summary>Captures staged envelopes and the message id each was staged under.</summary>
internal sealed class FakeOutboxStaging : IOutboxStaging
{
    public List<(IntegrationEventEnvelope Envelope, Guid TenantId, Guid? DeterministicId)> Staged { get; } = [];

    public void Stage(IntegrationEventEnvelope envelope, Guid tenantId, Guid? deterministicId = null) =>
        Staged.Add((envelope, tenantId, deterministicId));
}
