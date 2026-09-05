using TaskFlow.Domain.Model.ValueObjects;

namespace Test.Unit.Domain;

/// <summary>
/// Validates recurrence expansion: the arithmetic per frequency, the end-date clamp, and the per-run ceiling.
/// This is the logic that decides how many task rows a generation run creates, so an off-by-one here is an
/// off-by-one in the database.
/// Pure-unit tier: the value object is the SUT.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class RecurrencePatternTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Daily with interval 1 produces one occurrence per day up to and including the as-of instant.</summary>
    [TestMethod]
    public void Expand_Daily_ProducesOneOccurrencePerDay()
    {
        var pattern = new RecurrencePattern { Frequency = RecurrencePattern.Daily, Interval = 1 };

        var occurrences = pattern.Expand(Start, Start.AddDays(3), 12);

        CollectionAssert.AreEqual(
            new[] { Start, Start.AddDays(1), Start.AddDays(2), Start.AddDays(3) },
            occurrences.ToArray());
    }

    /// <summary>Weekly steps seven days per interval.</summary>
    [TestMethod]
    public void Expand_Weekly_StepsSevenDaysPerInterval()
    {
        var pattern = new RecurrencePattern { Frequency = RecurrencePattern.Weekly, Interval = 2 };

        var occurrences = pattern.Expand(Start, Start.AddDays(30), 12);

        CollectionAssert.AreEqual(
            new[] { Start, Start.AddDays(14), Start.AddDays(28) },
            occurrences.ToArray());
    }

    /// <summary>Monthly steps calendar months from the previous occurrence.</summary>
    [TestMethod]
    public void Expand_Monthly_StepsCalendarMonths()
    {
        var march1 = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
        var pattern = new RecurrencePattern { Frequency = RecurrencePattern.Monthly, Interval = 1 };

        var occurrences = pattern.Expand(march1, march1.AddMonths(2), 12);

        CollectionAssert.AreEqual(
            new[] { march1, march1.AddMonths(1), march1.AddMonths(2) },
            occurrences.ToArray());
    }

    /// <summary>
    /// A month-end series clamps and keeps the clamped day, because each step is taken from the previous
    /// occurrence rather than from the series anchor. Pinned here so the documented shortcut in
    /// <c>RecurrencePattern.Advance</c> cannot change silently.
    /// </summary>
    [TestMethod]
    public void Expand_MonthEnd_ClampsAndKeepsTheClampedDay()
    {
        var january31 = new DateTimeOffset(2026, 1, 31, 9, 0, 0, TimeSpan.Zero);
        var pattern = new RecurrencePattern { Frequency = RecurrencePattern.Monthly, Interval = 1 };

        var occurrences = pattern.Expand(january31, january31.AddMonths(3), 12);

        Assert.AreEqual(31, occurrences[0].Day);
        Assert.AreEqual(28, occurrences[1].Day, "February clamps to the last day of the month");
        Assert.AreEqual(28, occurrences[2].Day, "and the series continues from the clamped day");
    }

    /// <summary>Expansion stops at the end date even when more occurrences are due.</summary>
    [TestMethod]
    public void Expand_StopsAtEndDate()
    {
        var pattern = new RecurrencePattern
        {
            Frequency = RecurrencePattern.Daily,
            Interval = 1,
            EndDate = Start.AddDays(2)
        };

        var occurrences = pattern.Expand(Start, Start.AddDays(10), 12);

        Assert.HasCount(3, occurrences);
        Assert.AreEqual(Start.AddDays(2), occurrences[^1]);
    }

    /// <summary>A far-behind template yields at most the per-run ceiling, leaving the rest for the next run.</summary>
    [TestMethod]
    public void Expand_CapsAtMaxOccurrencesPerRun()
    {
        var pattern = new RecurrencePattern { Frequency = RecurrencePattern.Daily, Interval = 1 };

        var occurrences = pattern.Expand(Start, Start.AddDays(365), RecurrencePattern.MaxOccurrencesPerRun);

        Assert.HasCount(RecurrencePattern.MaxOccurrencesPerRun, occurrences);
    }

    /// <summary>An interval below one is treated as one rather than looping forever on a zero step.</summary>
    [TestMethod]
    public void Expand_ZeroInterval_StepsOneUnit()
    {
        var pattern = new RecurrencePattern { Frequency = RecurrencePattern.Daily, Interval = 0 };

        var occurrences = pattern.Expand(Start, Start.AddDays(2), 12);

        Assert.HasCount(3, occurrences);
    }

    /// <summary>An unsupported frequency yields nothing and has no next occurrence.</summary>
    [TestMethod]
    public void UnsupportedFrequency_ExpandsToNothing()
    {
        var pattern = new RecurrencePattern { Frequency = "Fortnightly", Interval = 1 };

        Assert.IsFalse(RecurrencePattern.IsSupportedFrequency(pattern.Frequency));
        Assert.IsEmpty(pattern.Expand(Start, Start.AddDays(30), 12));
        Assert.IsNull(pattern.Next(Start));
    }

    /// <summary>Next returns null once the series passes its end date, which is how the generator stops.</summary>
    [TestMethod]
    public void Next_PastEndDate_ReturnsNull()
    {
        var pattern = new RecurrencePattern
        {
            Frequency = RecurrencePattern.Weekly,
            Interval = 1,
            EndDate = Start.AddDays(3)
        };

        Assert.IsNull(pattern.Next(Start));
    }
}
