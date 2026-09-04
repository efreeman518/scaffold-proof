namespace TaskFlow.Domain.Model.ValueObjects;

/// <summary>Models recurrence pattern domain behavior and invariants.</summary>
public class RecurrencePattern
{
    public int Interval { get; init; }
    public string Frequency { get; init; } = null!;
    public DateTimeOffset? EndDate { get; init; }

    // Only methods and constants below: this type is mapped as an owned JSON document, so any new
    // readable property would become a JSON member and change the stored shape.

    /// <summary>Ceiling on occurrences generated for one template in one generation run.</summary>
    public const int MaxOccurrencesPerRun = 12;

    public const string Daily = "Daily";
    public const string Weekly = "Weekly";
    public const string Monthly = "Monthly";

    /// <summary>True when the frequency is one the generator knows how to advance.</summary>
    public static bool IsSupportedFrequency(string? frequency) =>
        string.Equals(frequency, Daily, StringComparison.OrdinalIgnoreCase)
        || string.Equals(frequency, Weekly, StringComparison.OrdinalIgnoreCase)
        || string.Equals(frequency, Monthly, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Occurrence timestamps starting at <paramref name="firstOccurrenceUtc"/> that are already due at
    /// <paramref name="asOfUtc"/>, at most <paramref name="max"/> of them and never past <see cref="EndDate"/>.
    /// A template that has fallen far behind catches up <paramref name="max"/> occurrences per run rather
    /// than materializing an unbounded backlog in one transaction.
    /// </summary>
    public IReadOnlyList<DateTimeOffset> Expand(DateTimeOffset firstOccurrenceUtc, DateTimeOffset asOfUtc, int max)
    {
        if (max <= 0 || !IsSupportedFrequency(Frequency)) return [];

        var occurrences = new List<DateTimeOffset>();
        var cursor = firstOccurrenceUtc;
        while (occurrences.Count < max && cursor <= asOfUtc && (EndDate is null || cursor <= EndDate))
        {
            occurrences.Add(cursor);
            cursor = Advance(cursor);
        }

        return occurrences;
    }

    /// <summary>
    /// The occurrence after <paramref name="occurrenceUtc"/>, or null once the series has passed
    /// <see cref="EndDate"/> - which is how the generator learns to stop scheduling this template.
    /// </summary>
    public DateTimeOffset? Next(DateTimeOffset occurrenceUtc)
    {
        if (!IsSupportedFrequency(Frequency)) return null;
        var next = Advance(occurrenceUtc);
        return EndDate is not null && next > EndDate ? null : next;
    }

    /// <summary>Steps one interval. Monthly uses calendar months, so the 31st clamps the way AddMonths does.</summary>
    private DateTimeOffset Advance(DateTimeOffset from)
    {
        var step = Interval < 1 ? 1 : Interval;
        return Frequency.ToUpperInvariant() switch
        {
            "DAILY" => from.AddDays(step),
            "WEEKLY" => from.AddDays(7 * step),
            "MONTHLY" => from.AddMonths(step),
            _ => throw new NotSupportedException($"Recurrence frequency '{Frequency}' is not supported.")
        };
    }
}
