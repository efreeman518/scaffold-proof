using EF.Data.Contracts;
using EF.Domain.Contracts;

namespace TaskFlow.Application.Contracts.Concurrency;

/// <summary>
/// The filter for every catch that turns a failed write into a failure Result (D-032). A cancellation (client
/// disconnect or request timeout) must reach the host so it answers 499/504 instead of a 400, and a lost update
/// must reach it as 412 (<see cref="ConcurrencyGuard.IsConcurrencyFailure"/>); everything else becomes a Result
/// carrying a fixed message - the provider's own text (schema, table, key values) stays in the log.
/// </summary>
public static class SaveFailure
{
    /// <summary>True when a save exception may be converted into a failure Result.</summary>
    public static bool MapsToFailureResult(Exception ex) =>
        ex is not OperationCanceledException && !ConcurrencyGuard.IsConcurrencyFailure(ex);

    /// <summary>
    /// The failure for a save exception <see cref="MapsToFailureResult"/> accepted, always with the fixed
    /// <see cref="ErrorConstants.ERROR_SAVE_FAILED"/> text: a constraint violation is a conflict with stored data (409),
    /// anything else an unexpected failure (500, no detail sent).
    /// </summary>
    public static Error ToError(Exception ex) => ex is DatabaseConstraintException
        ? new ConflictError(ErrorConstants.ERROR_SAVE_FAILED, "save.constraint")
        : new UnexpectedError(ErrorConstants.ERROR_SAVE_FAILED, ex, "save.failed");
}
