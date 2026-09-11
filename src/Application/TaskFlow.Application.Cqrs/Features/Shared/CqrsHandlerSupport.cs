using EF.Common.Contracts;
using EF.CQRS.Validation;
using EF.Data.Contracts;
using Microsoft.Extensions.Logging;
using TaskFlow.Application.Contracts.Concurrency;
using TaskFlow.Application.Models.Paging;

namespace TaskFlow.Application.Cqrs.Shared;

/// <summary>
/// Shared CQRS handler helpers for behavior that must match service-style handlers:
/// cancellation handling, optimistic save policy, and validator bridging. Integration events are staged
/// by the persistence interceptor (D-026), never published from a handler.
/// </summary>
internal static class CqrsHandlerSupport
{
    /// <summary>Searches search and returns filtered results for callers.</summary>
    public static async Task<PagedResponse<TDto>> SearchAsync<TDto>(
        Func<CancellationToken, Task<PagedResponse<TDto>>> search,
        ILogger logger,
        string operation,
        CancellationToken ct)
    {
        try
        {
            return await search(ct);
        }
        catch (OperationCanceledException)
        {
            logger.SearchCancelled(operation);
            return new PagedResponse<TDto>();
        }
    }

    /// <summary>Keyset variant of <see cref="SearchAsync{TDto}"/> for the cursor-paged TaskItem list.</summary>
    public static async Task<CursorPage<TDto>> SearchCursorAsync<TDto>(
        Func<CancellationToken, Task<CursorPage<TDto>>> search,
        ILogger logger,
        string operation,
        CancellationToken ct)
    {
        try
        {
            return await search(ct);
        }
        catch (OperationCanceledException)
        {
            logger.SearchCancelled(operation);
            return new CursorPage<TDto>([], null, false);
        }
    }

    /// <summary>
    /// Saves with the throwing concurrency policy and maps non-concurrency failures to a Result. The
    /// exception filter is load-bearing: without it a lost-update failure would be caught here and
    /// returned as a generic 400 instead of reaching the handler as a 412.
    /// </summary>
    public static async Task<Result> TrySaveAsync(
        IRepositoryBase repository,
        ILogger logger,
        string errorMessage,
        CancellationToken ct,
        params object?[] args)
    {
        try
        {
            await ConcurrencyGuard.SaveAsync(repository, ct);
            return Result.Success();
        }
        catch (Exception ex) when (!ConcurrencyGuard.IsConcurrencyFailure(ex))
        {
            logger.SaveFailed(ex, errorMessage, args);
            return Result.Failure(ex.GetBaseException().Message);
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
