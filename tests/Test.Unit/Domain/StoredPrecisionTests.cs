using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;

namespace Test.Unit.Domain;

/// <summary>D-033: values are normalized to the precision the database stores, in the domain.</summary>
[TestClass]
public class StoredPrecisionTests
{
    /// <summary>Effort rounds to two decimals; the midpoint goes away from zero, not to even.</summary>
    [DataRow("1.23456", "1.23")]
    [DataRow("1.235", "1.24")]
    [DataRow("1.225", "1.23")]
    [DataRow("-1.235", "-1.24")]
    [DataRow("2", "2")]
    [TestMethod]
    public void Effort_RoundsToScaleAwayFromZero(string value, string expected) =>
        Assert.AreEqual(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            StoredPrecision.Effort(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>A null stays null.</summary>
    [TestMethod]
    public void NullValues_StayNull()
    {
        Assert.IsNull(StoredPrecision.Effort(null));
        Assert.IsNull(StoredPrecision.Timestamp((DateTimeOffset?)null));
    }

    /// <summary>Sub-microsecond ticks are dropped and the offset is kept.</summary>
    [TestMethod]
    public void Timestamp_TruncatesToWholeMicrosecondsKeepingOffset()
    {
        var value = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)).AddTicks(1234567);
        var result = StoredPrecision.Timestamp(value);

        Assert.AreEqual(value.Ticks - 7, result.Ticks);
        Assert.AreEqual(value.Offset, result.Offset);
        Assert.AreEqual(result, StoredPrecision.Timestamp(result));
    }

    /// <summary>Every TaskItem setter path stores the normalized value.</summary>
    [TestMethod]
    public void TaskItem_NormalizesEffortAndDates()
    {
        var item = TaskItem.Create(TenantId.From(Guid.NewGuid()), "Normalize").Value!;
        var ticks = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero).AddTicks(9);

        item.Update(estimatedEffort: 1.23456m, actualEffort: 0.005m);
        item.UpdateDateRange(ticks, ticks);

        Assert.AreEqual(1.23m, item.EstimatedEffort);
        Assert.AreEqual(0.01m, item.ActualEffort);
        Assert.AreEqual(ticks.Ticks - 9, item.StartDate!.Value.Ticks);
        Assert.AreEqual(ticks.Ticks - 9, item.DueDate!.Value.Ticks);

        var occurrence = TaskItem.CreateOccurrence(TenantId.From(Guid.NewGuid()), TaskItemId.From(Guid.NewGuid()),
            TaskItemId.From(Guid.NewGuid()), ticks, "Occurrence").Value!;
        Assert.AreEqual(ticks.Ticks - 9, occurrence.DueDate!.Value.Ticks);
    }
}
