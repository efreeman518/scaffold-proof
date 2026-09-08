using System.Buffers;
using System.Text;
using TaskFlow.Infrastructure.Data.Operational;

namespace TaskFlow.Infrastructure.Data.Messaging;

/// <summary>
/// One pooled UTF-8 buffer holding the message bodies of a whole outbox batch (D-047 hot path). Both
/// transports hand the broker a slice of this buffer instead of allocating a <c>byte[]</c> per row: a
/// 50-row batch costs one rental rather than 50 arrays, on every dispatcher poll for the life of the process.
/// The row payload is already a string by the time it leaves the database, so this removes the copy that
/// <em>is</em> avoidable - the encode allocation - not the one that is not.
///
/// Lifetime: the returned slices point into the rented array and are valid only until <see cref="Dispose"/>.
/// The broker holds a body until its send completes, so the owner must dispose AFTER awaiting the publish,
/// never per message. Rent it once per <c>SendBatchAsync</c> call rather than caching one per transport
/// instance: the outbox dispatcher sends destination groups concurrently (D-055), so a shared buffer would
/// be a data race.
/// </summary>
public sealed class OutboxBodyBuffer : IDisposable
{
    private byte[] _buffer;
    private int _written;
    private bool _disposed;

    private OutboxBodyBuffer(byte[] buffer) => _buffer = buffer;

    /// <summary>Rents a buffer sized to the exact UTF-8 length of every payload in the batch.</summary>
    /// <param name="messages">Rows whose payloads will be appended, in any order.</param>
    public static OutboxBodyBuffer Rent(IReadOnlyList<OutboxMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        // Exact byte count, not GetMaxByteCount: the max is 3x the char count, which on a batch of
        // kilobyte envelopes would ask the pool for megabytes and fall out of its bucket sizes into a
        // plain allocation - exactly what this type exists to avoid. Counting is one cheap extra pass.
        var total = 0;
        for (var i = 0; i < messages.Count; i++)
            total += Encoding.UTF8.GetByteCount(messages[i].Payload);

        return new OutboxBodyBuffer(ArrayPool<byte>.Shared.Rent(total));
    }

    /// <summary>Encodes one payload into the buffer and returns the slice holding it.</summary>
    /// <param name="payload">Envelope JSON from the outbox row.</param>
    /// <returns>The encoded body; invalid after <see cref="Dispose"/>.</returns>
    public ReadOnlyMemory<byte> Append(string payload)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var count = Encoding.UTF8.GetBytes(payload, _buffer.AsSpan(_written));
        var slice = new ReadOnlyMemory<byte>(_buffer, _written, count);
        _written += count;
        return slice;
    }

    /// <summary>Returns the buffer to the pool; every slice handed out becomes invalid.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // No clearArray: the contents are the tenant's own event payloads that were just published to a
        // broker they can read, so a later renter seeing stale bytes discloses nothing new. Clearing a
        // batch-sized buffer on every poll would be pure cost.
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
    }
}
