using EF.Data.Contracts;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;

namespace TaskFlow.Application.Contracts.Repositories;

/// <summary>Persists and queries i attachment data through infrastructure storage contracts.</summary>
public interface IAttachmentRepositoryTrxn : IRepositoryTrxn<Attachment, AttachmentId>
{
    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    Task<Attachment?> GetAttachmentAsync(AttachmentId id, CancellationToken ct = default);

    /// <summary>
    /// Deletes the attachment row in one <see cref="OptimisticConcurrencyWinner.Throw"/> save that also stages the
    /// deferred delete of its uploaded content, by the stored key (D-026), so the row and its blob go together; a
    /// metadata-only attachment has no content and stages nothing. A write failure after which the row is gone (a commit
    /// that landed but was reported failed and re-sent by the provider strategy, or a competing delete) is rethrown as a
    /// lost optimistic save, so <see cref="IRepositoryBase.RetryOnConcurrencyAsync"/> re-reads and finds it gone (D-073);
    /// while the row is still stored, the original failure propagates unchanged.
    /// </summary>
    Task DeleteAttachmentAsync(Attachment attachment, CancellationToken ct = default);

    /// <summary>
    /// Saves the unit of work with a reservation for content an upload is about to write under
    /// <paramref name="storageKey"/>: a blob-delete work row the worker cannot claim before the configured grace period
    /// (<c>AttachmentUploadSettings.OrphanBlobGrace</c>). When the attachment row is never inserted, the reservation
    /// deletes the orphaned blob (D-075). Returns the reservation id for <see cref="InsertUploadedAsync"/>.
    /// </summary>
    Task<Guid> ReserveUploadAsync(Guid tenantId, string storageKey, CancellationToken ct = default);

    /// <summary>
    /// Inserts the uploaded attachment and removes its reservation in one transaction under the context's execution
    /// strategy. The removal is guarded on the lease: when the worker has claimed, parked or settled the reservation, it
    /// removes no row, the transaction rolls back and a <c>DbUpdateConcurrencyException</c>
    /// propagates, so no attachment row points at a blob the reservation deletes. Any other failure (a same-id insert
    /// race included) also rolls back and propagates unchanged.
    /// </summary>
    Task InsertUploadedAsync(Attachment attachment, Guid reservationId, CancellationToken ct = default);
}
