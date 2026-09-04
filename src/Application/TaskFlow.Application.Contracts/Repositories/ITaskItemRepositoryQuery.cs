using EF.Data.Contracts;
using TaskFlow.Application.Models;
using TaskFlow.Application.Contracts.Paging;
using TaskFlow.Application.Models.Paging;
using TaskFlow.Application.Models.Reads;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;

namespace TaskFlow.Application.Contracts.Repositories;

/// <summary>Persists and queries i task item data through infrastructure storage contracts.</summary>
public interface ITaskItemRepositoryQuery : IRepositoryQuery<TaskItem, TaskItemId>
{
    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    Task<TaskItem?> GetTaskItemAsync(TaskItemId id, CancellationToken ct = default);

    /// <summary>
    /// Keyset page of task items. <paramref name="after"/> is the decoded cursor position; null starts
    /// at the first page. Returns one extra row internally to decide <c>HasMore</c> without a count.
    /// </summary>
    Task<(IReadOnlyList<TaskItemDto> Data, bool HasMore)> SearchTaskItemsAsync(
        TaskItemCursorSearchRequest request, CursorToken? after, CancellationToken ct = default);

    /// <summary>Tenant task counts by status plus overdue and total in a single aggregate query.</summary>
    Task<TaskItemSummaryDto> GetSummaryAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>One export batch ordered by Id, resuming after <paramref name="afterId"/>.</summary>
    IAsyncEnumerable<TaskItemExportDto> StreamExportAsync(Guid tenantId, Guid? afterId, int batchSize, CancellationToken ct = default);

    /// <summary>
    /// Equality lookup on the encrypted <c>SecureDeterministic</c> column through its blind index (D-023):
    /// the ciphertext itself is randomized, so equality is proven on the keyed HMAC of the plaintext token.
    /// </summary>
    Task<TaskItem?> FindBySecureTokenAsync(string secureDeterministic, CancellationToken ct = default);
}
