using EF.Common.Contracts;

namespace TaskFlow.Application.Contracts.Concurrency;

/// <summary>
/// Caller-supplied create ids must be UUIDv7 (GR-17). Time-ordered ids keep clustered-index inserts
/// sequential; a random v4 from a client would fragment every table it lands in.
/// </summary>
public static class UuidV7
{
    /// <summary>True when the version nibble of the RFC 9562 layout is 7. Guid.Empty is never a v7 id -
    /// it carries no version nibble and is not an absent id (that is Guid?.HasValue == false), so it
    /// must fail like any other non-v7 value rather than slip through as valid.</summary>
    public static bool IsV7(Guid id) => id != Guid.Empty && (id.ToByteArray(bigEndian: true)[6] >> 4) == 7;

    /// <summary>
    /// The instant carried by a UUIDv7's leading 48-bit big-endian Unix-millisecond field (RFC 9562).
    /// <para>
    /// Both audit sinks derive the recorded time and their row keys from the audit message's own id rather
    /// than from the writing process's clock: an audit message can be redelivered, and a key built from
    /// the write clock would land a replay on a second row instead of overwriting the first.
    /// </para>
    /// Throws for a non-v7 id: there is no embedded instant to read, and inventing one would reintroduce
    /// the duplicate this exists to prevent.
    /// </summary>
    public static DateTimeOffset TimestampOf(Guid id)
    {
        if (!IsV7(id))
            throw new ArgumentException(string.Format(ErrorConstants.ERROR_ID_NOT_UUID_V7, id), nameof(id));

        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes, bigEndian: true, out _);

        var unixMilliseconds = ((long)bytes[0] << 40) | ((long)bytes[1] << 32) | ((long)bytes[2] << 24)
            | ((long)bytes[3] << 16) | ((long)bytes[4] << 8) | bytes[5];

        return DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds);
    }

    /// <summary>
    /// Validates an optional caller-supplied create id. Absent (null) is valid - the server generates
    /// one; present but not v7, including Guid.Empty, fails so the endpoint answers 400 rather than
    /// persisting a bad key.
    /// </summary>
    public static Result ValidateCallerId(Guid? id) =>
        id is null || IsV7(id.Value)
            ? Result.Success()
            : Result.Failure(string.Format(ErrorConstants.ERROR_ID_NOT_UUID_V7, id.Value));
}
