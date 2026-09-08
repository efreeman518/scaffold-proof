using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace TaskFlow.Infrastructure.Repositories;

/// <summary>Decoded keyset position: the last row of the page the token was minted from.</summary>
/// <param name="LastModifiedUtc">Sort key of that row, as the database returned it.</param>
/// <param name="Id">Tie-break key of that row.</param>
public readonly record struct TaskViewKeysetPosition(DateTimeOffset LastModifiedUtc, string Id);

/// <summary>
/// Opaque continuation token for the relational TaskView list query (D-038). Base64Url over
/// <c>v1|tenantId|lastModifiedUtcTicks|id</c>, so <c>TaskViewPage.ContinuationToken</c> stays a black box to
/// <c>TaskViewEndpoints</c> exactly as the Cosmos token is, and the encoding lives in one place instead of
/// being split between the minting and the parsing side.
///
/// No MAC: unlike the TaskItem search cursor, this token carries no authority. The tenant is re-checked
/// against the requested tenant on decode and the query is tenant-filtered server-side, so the most a forged
/// token can do is reposition the caller inside their own tenant - the same property the unauthenticated
/// Cosmos continuation token has. A structurally invalid token is a caller error (400), never silently
/// treated as "start from the beginning", which would loop a paging client forever.
/// </summary>
public static class TaskViewKeysetToken
{
    private const string Version = "v1";
    private const char Separator = '|';

    /// <summary>Mints the token that resumes after the given row.</summary>
    public static string Encode(string tenantId, DateTimeOffset lastModifiedUtc, string id)
    {
        // Ticks, not "O": the value is echoed back into a WHERE clause and must compare bit-identical to
        // what the provider stored (PostgreSQL timestamptz keeps microseconds, SQL Server keeps 100ns).
        var payload = string.Join(Separator,
            Version,
            tenantId,
            lastModifiedUtc.UtcTicks.ToString(CultureInfo.InvariantCulture),
            id);

        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload));
    }

    /// <summary>
    /// Decodes a token for <paramref name="expectedTenantId"/>. Throws <see cref="ArgumentException"/> for a
    /// malformed, truncated, or foreign-tenant token; the API's global handler maps that to 400.
    /// </summary>
    public static TaskViewKeysetPosition Decode(string token, string expectedTenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        string payload;
        try
        {
            payload = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(token));
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The continuation token is not a valid TaskView token.", nameof(token), ex);
        }

        var parts = payload.Split(Separator);
        if (parts.Length != 4
            || !string.Equals(parts[0], Version, StringComparison.Ordinal)
            || !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
            || ticks < 0 || ticks > DateTimeOffset.MaxValue.UtcTicks
            || parts[3].Length == 0)
        {
            throw new ArgumentException("The continuation token is not a valid TaskView token.", nameof(token));
        }

        if (!string.Equals(parts[1], expectedTenantId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The continuation token was issued for a different tenant.", nameof(token));
        }

        return new TaskViewKeysetPosition(new DateTimeOffset(ticks, TimeSpan.Zero), parts[3]);
    }
}
