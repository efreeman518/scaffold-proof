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
    /// metadata-only attachment has no content and stages nothing. A write failure after which the row is gone means the
    /// delete already landed (a commit reported failed, which the provider strategy re-sends, and the re-sent work-row
    /// insert fails on its key) or another request removed it first: it is rethrown as a lost optimistic save, so
    /// <see cref="IRepositoryBase.RetryOnConcurrencyAsync"/> re-reads and finds it gone (D-073). Any other failure
    /// propagates unchanged.
    /// </summary>
    Task DeleteAttachmentAsync(Attachment attachment, CancellationToken ct = default);
}
