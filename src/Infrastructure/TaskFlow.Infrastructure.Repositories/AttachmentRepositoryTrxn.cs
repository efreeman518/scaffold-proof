using EF.Data;
using EF.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.Contracts.Storage;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Infrastructure.Data;

namespace TaskFlow.Infrastructure.Repositories;

/// <summary>Persists and queries attachment data through infrastructure storage contracts.</summary>
public class AttachmentRepositoryTrxn(TaskFlowDbContextTrxn db, IOptions<AttachmentUploadSettings>? uploadSettings = null)
    : TaskFlowRepositoryTrxn<Attachment, AttachmentId>(db), IAttachmentRepositoryTrxn
{
    private readonly TimeSpan _orphanBlobGrace = (uploadSettings?.Value ?? new AttachmentUploadSettings()).OrphanBlobGrace;

    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    public async Task<Attachment?> GetAttachmentAsync(AttachmentId id, CancellationToken ct = default)
    {
        return await GetEntityAsync(
            true,
            filter: (Attachment a) => a.Id == id,
            cancellationToken: ct
        ).ConfigureAwait(ConfigureAwaitOptions.None);
    }

    /// <inheritdoc />
    public async Task DeleteAttachmentAsync(Attachment attachment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        Delete(attachment);
        if (attachment.StorageKey is { } storageKey)
        {
            DB.BlobDeleteWork.Add(AttachmentBlobDeleteWork.ForAttachment(
                attachment.TenantId.Value, attachment.Id.Value, storageKey, DB.Clock.GetUtcNow()));
        }

        try
        {
            await DB.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, cancellationToken: ct).ConfigureAwait(ConfigureAwaitOptions.None);
        }
        catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException)
        {
            // Provider-neutral, as SaveChildAddAsync: the existence read decides, not the provider's error code.
            var id = attachment.Id;
            if (await DB.Set<Attachment>().AsNoTracking().AnyAsync(a => a.Id == id, ct).ConfigureAwait(ConfigureAwaitOptions.None)) throw;
            throw new DbUpdateConcurrencyException("The attachment row was already deleted when this save was sent.", ex);
        }
    }

    /// <inheritdoc />
    public async Task<Guid> ReserveUploadAsync(Guid tenantId, string storageKey, CancellationToken ct = default)
    {
        var reservation = AttachmentBlobDeleteWork.UploadReservation(tenantId, storageKey, DB.Clock.GetUtcNow() + _orphanBlobGrace);
        DB.BlobDeleteWork.Add(reservation);
        await DB.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, cancellationToken: ct).ConfigureAwait(ConfigureAwaitOptions.None);
        return reservation.Id;
    }

    /// <inheritdoc />
    // The work row carries no concurrency token, but its delete still expects one affected row: when the worker has
    // already claimed and completed the reservation (an upload that outlived the grace period), the save fails as a lost
    // save, so no attachment row is left pointing at the blob the worker deleted.
    public void ReleaseUploadReservation(Guid reservationId) =>
        DB.BlobDeleteWork.Remove(DB.BlobDeleteWork.Local.Single(w => w.Id == reservationId));
}
