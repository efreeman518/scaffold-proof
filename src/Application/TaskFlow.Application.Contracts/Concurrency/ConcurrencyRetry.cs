using EF.Common.Contracts;
using EF.Data.Contracts;

namespace TaskFlow.Application.Contracts.Concurrency;

/// <summary>
/// D-073: the fresh-read retry for writes whose caller stated no precondition, in the service and the CQRS style
/// alike: the child adds, which carry no If-Match (comment, checklist item, tag association; their save is
/// <c>ITaskItemRepositoryTrxn.SaveChildAddAsync</c>, which turns a same-key insert race into a lost save), and the edits
/// and deletes sent with <c>If-Match: *</c> (D-032). A concrete If-Match is never retried: it is the caller's statement
/// of the version it decided on, so a lost race stays 412.
/// </summary>
/// <remarks>
/// The <c>work</c> of every method reads everything it changes, makes one
/// <c>SaveChangesAsync(OptimisticConcurrencyWinner.Throw)</c> save and has no other outside effect (events go through
/// the outbox interceptor and blob deletes are work rows staged in that save; cache eviction runs after it returns). The
/// change tracker must hold no pending change when it is called: <see cref="IRepositoryBase.RetryOnConcurrencyAsync"/>
/// clears it before each attempt and refuses pending changes rather than discarding them.
/// </remarks>
public static class ConcurrencyRetry
{
    /// <summary>
    /// Runs a child add's <paramref name="work"/> (read the root, decide, one save) inside
    /// <see cref="IRepositoryBase.RetryOnConcurrencyAsync"/>. When every attempt loses the race the caller, who sent no
    /// precondition, gets 409 (<see cref="ConflictException"/>), not the 412 of a stale If-Match. The add paths run no
    /// <see cref="ConcurrencyGuard.Require"/>, so every concurrency failure reaching here is an exhausted retry.
    /// </summary>
    public static async Task<T> RunAsync<T>(
        IRepositoryBase repository, string entityType, Guid taskItemId, Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        try
        {
            return await repository.RetryOnConcurrencyAsync(work, cancellationToken: ct);
        }
        catch (Exception ex) when (ConcurrencyGuard.IsConcurrencyFailure(ex))
        {
            throw new ConflictException(
                $"TaskItem {taskItemId} kept changing while the {entityType} was added; retry the request.", ex);
        }
    }

    /// <summary>
    /// Runs an If-Match write's <paramref name="work"/> (read, <see cref="ConcurrencyGuard.Require"/>, apply, one save).
    /// A concrete <paramref name="expectedVersion"/> runs it once, so a stale read or a lost save stays 412. The wildcard
    /// (<c>null</c>) runs it inside <see cref="IRepositoryBase.RetryOnConcurrencyAsync"/>, where the guard always passes
    /// and a lost save re-reads and applies again; a race lost on every attempt is 409 (<see cref="ConflictException"/>).
    /// </summary>
    public static async Task<T> RunAsync<T>(
        IRepositoryBase repository, long? expectedVersion, string entityType, Guid entityId,
        Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        if (expectedVersion is not null) return await work(ct);

        try
        {
            return await repository.RetryOnConcurrencyAsync(work, cancellationToken: ct);
        }
        catch (Exception ex) when (ConcurrencyGuard.IsConcurrencyFailure(ex))
        {
            throw new ConflictException(
                $"{entityType} {entityId} kept changing while the If-Match: * write was applied; retry the request.", ex);
        }
    }
}
