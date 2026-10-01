using EF.Common.Contracts;
using EF.Data.Contracts;

namespace TaskFlow.Application.Contracts.Concurrency;

/// <summary>
/// D-073: the fresh-read retry shared by the child adds that carry no If-Match (comment, checklist item, tag
/// association), in the service and the CQRS style alike. The save inside the work is
/// <c>ITaskItemRepositoryTrxn.SaveChildAddAsync</c>, which turns a same-key insert race into a lost save.
/// </summary>
public static class ChildAddRetry
{
    /// <summary>
    /// Runs <paramref name="work"/> (read the root, decide, one save) inside
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
}
