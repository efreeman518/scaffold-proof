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
public class AttachmentRepositoryTrxn(TaskFlowDbContextTrxn db, IOptions<AttachmentUploadSettings> uploadSettings)
    : TaskFlowRepositoryTrxn<Attachment, AttachmentId>(db), IAttachmentRepositoryTrxn
{
    private readonly TimeSpan _orphanBlobGrace =
        (uploadSettings ?? throw new ArgumentNullException(nameof(uploadSettings))).Value.OrphanBlobGrace;

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
    public Task InsertUploadedAsync(Attachment attachment, Guid reservationId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        if (!DB.Database.IsRelational())
            return InsertReleasingTrackedAsync(attachment, reservationId, ct);

        return ResilientTransaction.New(DB).ExecuteAsync(token => InsertReleasingAsync(attachment, reservationId, token), ct);
    }

    /// <summary>
    /// One attempt of <see cref="InsertUploadedAsync"/> inside its transaction. The execution strategy runs it again after a
    /// transient failure, also one whose commit landed but was reported failed, so every attempt starts from the insert.
    /// </summary>
    private async Task InsertReleasingAsync(Attachment attachment, Guid reservationId, CancellationToken ct)
    {
        DB.Entry(attachment).State = EntityState.Added;

        // Guarded on the lease: a reservation the worker has claimed (or parked) is not released, whatever the clock says.
        var released = await DB.BlobDeleteWork
            .Where(w => w.Id == reservationId && w.LeaseToken == null && w.DeadLetteredAtUtc == null)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);
        if (released == 0)
        {
            // The key is unique per upload, so a stored row with this id and key is this upload's own landed commit.
            var id = attachment.Id;
            var storageKey = attachment.StorageKey;
            if (await DB.Set<Attachment>().AsNoTracking().AnyAsync(a => a.Id == id && a.StorageKey == storageKey, ct)
                    .ConfigureAwait(ConfigureAwaitOptions.None))
            {
                DB.Entry(attachment).State = EntityState.Unchanged;
                return;
            }

            throw ReservationNotReleased(reservationId);
        }

        await DB.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, cancellationToken: ct).ConfigureAwait(ConfigureAwaitOptions.None);
    }

    /// <summary>
    /// The InMemory test provider has no <c>ExecuteDelete</c> and no transactions: the same lease guard, applied to the
    /// tracked reservation, and one save for the insert and the release.
    /// </summary>
    private async Task InsertReleasingTrackedAsync(Attachment attachment, Guid reservationId, CancellationToken ct)
    {
        var reservation = await DB.BlobDeleteWork
            .SingleOrDefaultAsync(w => w.Id == reservationId && w.LeaseToken == null && w.DeadLetteredAtUtc == null, ct)
            .ConfigureAwait(ConfigureAwaitOptions.None) ?? throw ReservationNotReleased(reservationId);
        DB.Entry(attachment).State = EntityState.Added;
        DB.BlobDeleteWork.Remove(reservation);
        await DB.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, cancellationToken: ct).ConfigureAwait(ConfigureAwaitOptions.None);
    }

    private static DbUpdateConcurrencyException ReservationNotReleased(Guid reservationId) =>
        new($"Upload reservation {reservationId} is claimed, parked or already settled by the blob-delete worker, so the " +
            "attachment row is not inserted; the reservation deletes the blob.");
}
