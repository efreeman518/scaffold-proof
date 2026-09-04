using System.Globalization;
using TaskFlow.Application.Models;
using TaskFlow.Application.Models.Paging;

namespace TaskFlow.Application.Contracts.Paging;

/// <summary>
/// Serialization of the keyset sort key carried inside a cursor. Both ends of the cursor - the
/// application service that mints one from the last row of a page and the repository that turns one
/// back into a WHERE clause - go through here so the two encodings cannot drift.
///
/// Format: <c>DateTimeOffset</c> round-trip "O", enums as their integer value, and null as "~", which
/// sorts last and matches the leading <c>DueDate == null</c> key used by the DueDate sort modes.
/// </summary>
public static class CursorKey
{
    /// <summary>Marker for a null sort key; ordered last in every mode that allows nulls.</summary>
    public const string NullMarker = "~";

    /// <summary>Builds the sort key of the last row on a page for the given mode.</summary>
    public static string From(TaskItemSortMode sortMode, TaskItemDto last) => sortMode switch
    {
        TaskItemSortMode.IdAsc => string.Empty,
        TaskItemSortMode.DueDateAsc or TaskItemSortMode.DueDateDesc =>
            last.DueDate?.ToString("O", CultureInfo.InvariantCulture) ?? NullMarker,
        TaskItemSortMode.ModifiedDesc =>
            (last.ModifiedAtUtc ?? default).ToString("O", CultureInfo.InvariantCulture),
        TaskItemSortMode.StatusThenId => ((int)last.Status).ToString(CultureInfo.InvariantCulture),
        _ => string.Empty
    };

    /// <summary>True when the key stands for a null sort value.</summary>
    public static bool IsNull(string sortKey) => sortKey == NullMarker;

    /// <summary>Parses a round-trip timestamp sort key.</summary>
    public static bool TryDate(string sortKey, out DateTimeOffset value) =>
        DateTimeOffset.TryParseExact(sortKey, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value);

    /// <summary>Parses an integer (enum) sort key.</summary>
    public static bool TryInt(string sortKey, out int value) =>
        int.TryParse(sortKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
