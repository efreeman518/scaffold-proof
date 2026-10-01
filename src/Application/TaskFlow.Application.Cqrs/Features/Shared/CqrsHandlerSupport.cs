using EF.Common.Contracts;
using EF.CQRS.Validation;
using EF.Data.Contracts;
using Microsoft.Extensions.Logging;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Contracts.Concurrency;
using TaskFlow.Application.Contracts.Repositories;

namespace TaskFlow.Application.Cqrs.Shared;

/// <summary>
/// Shared CQRS handler helpers for behavior that must match service-style handlers:
/// optimistic save policy and validator bridging. Integration events are staged by the persistence
/// interceptor (D-026), never published from a handler. Searches have no helper: a cancelled or timed-out
/// search propagates so the host answers 499/504 instead of a 200 with an empty page a pager would stop on.
/// </summary>
internal static class CqrsHandlerSupport
{
    /// <summary>
    /// Saves with the throwing concurrency policy and maps other failures to a Result with a fixed message.
    /// The exception filter is load-bearing: without it a lost-update failure would be caught here and
    /// returned as a generic 400 instead of reaching the handler as a 412, and a cancellation would become
    /// a 400 instead of the host's 499/504.
    /// </summary>
    public static Task<Result> TrySaveAsync(
        IRepositoryBase repository,
        ILogger logger,
        string errorMessage,
        CancellationToken ct,
        params object?[] args) =>
        TryWriteAsync(t => repository.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, t), logger, errorMessage, ct, args);

    /// <summary>
    /// Runs a repository write that ends in one <c>Throw</c> save (a unit the repository owns, such as a delete with
    /// its set-based pre-step) and maps its failures like <see cref="TrySaveAsync"/>.
    /// </summary>
    public static async Task<Result> TryWriteAsync(
        Func<CancellationToken, Task> write,
        ILogger logger,
        string errorMessage,
        CancellationToken ct,
        params object?[] args)
    {
        try
        {
            await write(ct);
            return Result.Success();
        }
        catch (Exception ex) when (SaveFailure.MapsToFailureResult(ex))
        {
            logger.SaveFailed(ex, errorMessage, args);
            return Result.Failure(ErrorConstants.ERROR_SAVE_FAILED);
        }
    }

    /// <summary>
    /// Saves a child add (D-073): a write failure after which <paramref name="callerKeyStored"/> finds the caller's key
    /// stored is a lost race for the retry; any other failure maps to a Result like <see cref="TrySaveAsync"/>.
    /// </summary>
    public static async Task<Result> TrySaveAddAsync(
        ITaskItemRepositoryTrxn repository,
        Func<CancellationToken, Task<bool>>? callerKeyStored,
        ILogger logger,
        string errorMessage,
        CancellationToken ct,
        params object?[] args)
    {
        try
        {
            await repository.SaveChildAddAsync(callerKeyStored, ct);
            return Result.Success();
        }
        catch (Exception ex) when (SaveFailure.MapsToFailureResult(ex))
        {
            logger.SaveFailed(ex, errorMessage, args);
            return Result.Failure(ErrorConstants.ERROR_SAVE_FAILED);
        }
    }

    /// <summary>Converts the current value to validation result.</summary>
    public static RequestValidationResult ToValidationResult<T>(Result<T> result)
    {
        if (result.IsSuccess) return RequestValidationResult.Valid();
        if (result.Errors.Count > 0) return RequestValidationResult.Failure(result.Errors);
        return RequestValidationResult.Failure(result.ErrorMessage ?? "Validation failed.");
    }
}
