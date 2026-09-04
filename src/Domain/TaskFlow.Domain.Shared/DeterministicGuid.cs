using System.Security.Cryptography;
using System.Text;

namespace TaskFlow.Domain.Shared;

// fallback: replace with EF.Common.DeterministicGuid.Create when published (package request 12).
/// <summary>
/// RFC 4122 name-based UUID version 5 (SHA-1). The same namespace label and the same parts produce the
/// same Guid in every process, on every host, forever - which is what makes a replayed scheduler job
/// idempotent: it re-stages the row it staged last time instead of a second copy of the same event.
/// </summary>
public static class DeterministicGuid
{
    /// <summary>Fixed root namespace for every TaskFlow deterministic id. Changing it invalidates all of them.</summary>
    public static readonly Guid RootNamespace = new("8f1b1f3e-1a2c-4e58-9a5b-6f0f2e3d4c5a");

    /// <summary>
    /// Builds the UUIDv5 for <paramref name="ns"/> (a short label such as "overdue" or "recurrence") and
    /// <paramref name="parts"/>. Parts are joined with a separator that cannot occur in a Guid or an ISO-8601
    /// timestamp, so ("a","bc") and ("ab","c") never collide.
    /// </summary>
    public static Guid Create(string ns, params string[] parts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);
        ArgumentNullException.ThrowIfNull(parts);

        var name = parts.Length == 0 ? ns : ns + "|" + string.Join('|', parts);
        var nameBytes = Encoding.UTF8.GetBytes(name);

        Span<byte> input = stackalloc byte[16 + nameBytes.Length];
        WriteBigEndian(RootNamespace, input[..16]);
        nameBytes.CopyTo(input[16..]);

        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(input, hash);

        var result = hash[..16];
        // Version 5 in the high nibble of byte 6, RFC 4122 variant in the top two bits of byte 8.
        result[6] = (byte)((result[6] & 0x0F) | 0x50);
        result[8] = (byte)((result[8] & 0x3F) | 0x80);

        return ReadBigEndian(result);
    }

    /// <summary>Writes a Guid in RFC 4122 network byte order (the first three fields are big-endian).</summary>
    private static void WriteBigEndian(Guid value, Span<byte> destination)
    {
        value.TryWriteBytes(destination);
        Swap(destination, 0, 3);
        Swap(destination, 1, 2);
        Swap(destination, 4, 5);
        Swap(destination, 6, 7);
    }

    /// <summary>Reads a Guid back from RFC 4122 network byte order.</summary>
    private static Guid ReadBigEndian(Span<byte> source)
    {
        Swap(source, 0, 3);
        Swap(source, 1, 2);
        Swap(source, 4, 5);
        Swap(source, 6, 7);
        return new Guid(source);
    }

    private static void Swap(Span<byte> bytes, int left, int right) =>
        (bytes[left], bytes[right]) = (bytes[right], bytes[left]);
}
