using EF.Common.Contracts;
using EF.Data.Contracts;

namespace TaskFlow.Application.Contracts.Concurrency;

/// <summary>
/// The single optimistic-concurrency policy for the application layer (D-032). Every write path
/// checks the caller's expected version through <see cref="Require"/> after loading the aggregate and
/// before mutating it, then saves through <see cref="SaveAsync"/> so a lost update between load and
/// save still surfaces as <see cref="PreconditionFailedException"/> (also mapped to 412) instead of being
/// silently overwritten.
/// </summary>
public static class ConcurrencyGuard
{
    /// <summary>
    /// Enforces an If-Match precondition. A null <paramref name="expected"/> is the wildcard
    /// (<c>If-Match: *</c>) trusted-automation override and always passes.
    /// </summary>
    public static void Require(long? expected, long current, string entityType, Guid entityId)
    {
        if (expected is null) return;
        if (expected.Value != current)
            throw new ConcurrencyMismatchException(entityType, entityId, expected, current);
    }

    /// <summary>
    /// Saves with the throwing concurrency policy. <c>ClientWins</c> would reload the database values
    /// and re-apply the caller's stale copy, which is exactly the lost update the ETag contract exists
    /// to prevent.
    /// </summary>
    public static Task<int> SaveAsync(IRepositoryBase repository, CancellationToken ct) =>
        repository.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, ct);

    /// <summary>
    /// True when an exception raised by a save is a lost-update failure that must reach the caller as
    /// 412. Every catch-all around a save filters on this; without the filter a stale write would be
    /// converted into a generic 400 and the ETag contract would silently stop working.
    ///
    /// A throwing-policy save raises <see cref="PreconditionFailedException"/>; a policy-free save still raises
    /// the raw DbUpdateConcurrencyException, matched by type name rather than by type: the Application layer
    /// must not reference Microsoft.EntityFrameworkCore (Test.Architecture enforces that boundary), and
    /// introducing the reference only to name one exception would trade a real architectural rule for a keystroke.
    /// </summary>
    public static bool IsConcurrencyFailure(Exception ex) =>
        ex is PreconditionFailedException
        || ex.GetType().FullName == "Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException";

    /// <summary>
    /// The filter for every catch that turns a failed write into a failure Result. A cancellation (client
    /// disconnect or request timeout) must reach the host so it answers 499/504 instead of a 400, and a
    /// lost update must reach it as 412; everything else becomes a Result carrying a fixed message - the
    /// provider's own text (schema, table, key values) stays in the log.
    /// </summary>
    public static bool MapsToFailureResult(Exception ex) =>
        ex is not OperationCanceledException && !IsConcurrencyFailure(ex);
}
