using EF.Data.Contracts;

namespace Test.Architecture;

/// <summary>
/// Controls for <c>ConcurrencyArchitectureTests</c>: async saves in the shapes the throw-policy rule must flag
/// (policy-free, ClientWins) and the one shape it must accept (Throw). Never called; they exist only to be scanned.
/// </summary>
internal static class AsyncDirectSaveControl
{
    internal static async Task<int> SavePolicyFreeAsync(IRepositoryBase repository, CancellationToken ct) =>
        await repository.SaveChangesAsync(ct);

    internal static async Task<int> SaveClientWinsAsync(IRepositoryBase repository, CancellationToken ct) =>
        await repository.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, ct);

    internal static async Task<int> SaveThrowAsync(IRepositoryBase repository, CancellationToken ct) =>
        await repository.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, ct);
}
