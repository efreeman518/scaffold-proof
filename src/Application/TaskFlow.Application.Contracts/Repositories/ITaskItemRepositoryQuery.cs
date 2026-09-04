using EF.Common.Contracts;
using EF.Data.Contracts;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;

namespace TaskFlow.Application.Contracts.Repositories;

/// <summary>Persists and queries i task item data through infrastructure storage contracts.</summary>
public interface ITaskItemRepositoryQuery : IRepositoryQuery<TaskItem, TaskItemId>
{
    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    Task<TaskItem?> GetTaskItemAsync(TaskItemId id, CancellationToken ct = default);
    /// <summary>Searches search task items and returns filtered results for callers.</summary>
    Task<PagedResponse<TaskItemDto>> SearchTaskItemsAsync(SearchRequest<TaskItemSearchFilter> request, CancellationToken ct = default);
    /// <summary>
    /// Equality lookup on the encrypted <c>SecureDeterministic</c> column through its blind index (D-023):
    /// the ciphertext itself is randomized, so equality is proven on the keyed HMAC of the plaintext token.
    /// </summary>
    Task<TaskItem?> FindBySecureTokenAsync(string secureDeterministic, CancellationToken ct = default);
}
