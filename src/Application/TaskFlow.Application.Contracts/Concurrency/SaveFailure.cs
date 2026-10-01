using EF.Data.Contracts;

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
}
