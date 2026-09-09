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
    /// Validates an optional caller-supplied create id. Absent (null) is valid - the server generates
    /// one; present but not v7, including Guid.Empty, fails so the endpoint answers 400 rather than
    /// persisting a bad key.
    /// </summary>
    public static Result ValidateCallerId(Guid? id) =>
        id is null || IsV7(id.Value)
            ? Result.Success()
            : Result.Failure(string.Format(ErrorConstants.ERROR_ID_NOT_UUID_V7, id.Value));
}
