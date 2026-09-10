using EF.Common.Contracts;
using EF.Data.Contracts;
using TaskFlow.Application.Models;
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
    /// Keyset page of task items. The cursor in <paramref name="request"/> is decoded and the next one
    /// minted here, bound to <paramref name="tenantId"/> and the request sort mode, so a token from
    /// another tenant or another ordering fails closed with <see cref="ArgumentException"/> (400).
    /// </summary>
    Task<CursorPage<TaskItemDto>> SearchTaskItemsAsync(
        TaskItemCursorSearchRequest request, Guid tenantId, CancellationToken ct = default);

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
