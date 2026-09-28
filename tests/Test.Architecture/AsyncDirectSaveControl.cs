using EF.Data.Contracts;

namespace Test.Architecture;

/// <summary>
/// Positive control for <c>ConcurrencyArchitectureTests</c>: an async method that saves directly, the shape
/// the concurrency rule must flag. Never called; it exists only to be scanned.
/// </summary>
internal static class AsyncDirectSaveControl
{
    internal static async Task<int> SaveDirectlyAsync(IRepositoryBase repository, CancellationToken ct) =>
        await repository.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, ct);
}
