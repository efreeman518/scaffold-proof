using Microsoft.AspNetCore.DataProtection;
using TaskFlow.Application.Contracts.Paging;
using TaskFlow.Application.Models;
using TaskFlow.Application.Models.Paging;
using TaskFlow.Bootstrapper.Paging;
using TaskFlow.Domain.Shared.Enums;

namespace Test.Unit.Contracts;

/// <summary>
/// Unit coverage for the cursor primitives (GR-18): key serialization, page-size limits, and the
/// protector's three rejection paths. A cursor that decodes when it should not is a cross-tenant read;
/// a cursor that fails to round-trip is a broken page-through - both are cheapest to catch here.
/// Pure-unit tier: an ephemeral Data Protection provider, no host.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class CursorPagingContractTests
{
    private static ICursorProtector CreateProtector() =>
        new DataProtectionCursorProtector(DataProtectionProvider.Create(nameof(CursorPagingContractTests)));

    /// <summary>Verifies the page-size range is enforced at both edges.</summary>
    [TestMethod]
    public void Given_PageSizes_When_Validated_Then_OnlyTheRangeIsAccepted()
    {
        Assert.IsFalse(PageSizeLimits.IsValid(PageSizeLimits.Min - 1));
        Assert.IsTrue(PageSizeLimits.IsValid(PageSizeLimits.Min));
        Assert.IsTrue(PageSizeLimits.IsValid(PageSizeLimits.Default));
        Assert.IsTrue(PageSizeLimits.IsValid(PageSizeLimits.Max));
        Assert.IsFalse(PageSizeLimits.IsValid(PageSizeLimits.Max + 1));
    }

    /// <summary>Verifies sort keys serialize per mode, with null rendered as the last-sorting marker.</summary>
    [TestMethod]
    public void Given_LastRow_When_SortKeyBuilt_Then_MatchesModeContract()
    {
        var due = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var row = new TaskItemDto
        {
            Id = Guid.CreateVersion7(),
            Title = "row",
            DueDate = due,
            ModifiedAtUtc = due.AddHours(1),
            Status = TaskItemStatus.InProgress
        };

        Assert.AreEqual(string.Empty, CursorKey.From(TaskItemSortMode.IdAsc, row));
        Assert.AreEqual(due.ToString("O"), CursorKey.From(TaskItemSortMode.DueDateAsc, row));
        Assert.AreEqual(due.AddHours(1).ToString("O"), CursorKey.From(TaskItemSortMode.ModifiedDesc, row));
        Assert.AreEqual(((int)TaskItemStatus.InProgress).ToString(), CursorKey.From(TaskItemSortMode.StatusThenId, row));

        Assert.AreEqual(CursorKey.NullMarker, CursorKey.From(TaskItemSortMode.DueDateAsc, row with { DueDate = null }));
        Assert.IsTrue(CursorKey.IsNull(CursorKey.NullMarker));
        Assert.IsTrue(CursorKey.TryDate(due.ToString("O"), out var parsed));
        Assert.AreEqual(due, parsed);
    }

    /// <summary>Verifies a cursor round-trips through protection unchanged.</summary>
    [TestMethod]
    public void Given_CursorToken_When_ProtectedAndUnprotected_Then_RoundTrips()
    {
        var protector = CreateProtector();
        var tenantId = Guid.NewGuid();
        var token = new CursorToken(TaskItemSortMode.DueDateAsc, tenantId, "2026-06-01T12:00:00.0000000+00:00", Guid.CreateVersion7());

        var cursor = protector.Protect(token);

        Assert.IsTrue(protector.TryUnprotect(cursor, TaskItemSortMode.DueDateAsc, tenantId, out var decoded));
        Assert.AreEqual(token, decoded);
    }

    /// <summary>Verifies a cursor minted for one tenant cannot be replayed by another.</summary>
    [TestMethod]
    public void Given_ForeignTenant_When_Unprotect_Then_Fails()
    {
        var protector = CreateProtector();
        var cursor = protector.Protect(new CursorToken(TaskItemSortMode.IdAsc, Guid.NewGuid(), string.Empty, Guid.CreateVersion7()));

        Assert.IsFalse(protector.TryUnprotect(cursor, TaskItemSortMode.IdAsc, Guid.NewGuid(), out _));
    }

    /// <summary>Verifies a cursor is bound to the sort mode it was minted under.</summary>
    [TestMethod]
    public void Given_DifferentSortMode_When_Unprotect_Then_Fails()
    {
        var protector = CreateProtector();
        var tenantId = Guid.NewGuid();
        var cursor = protector.Protect(new CursorToken(TaskItemSortMode.IdAsc, tenantId, string.Empty, Guid.CreateVersion7()));

        Assert.IsFalse(protector.TryUnprotect(cursor, TaskItemSortMode.ModifiedDesc, tenantId, out _));
    }

    /// <summary>Verifies tampering is detected rather than producing a plausible-looking position.</summary>
    [TestMethod]
    public void Given_TamperedCursor_When_Unprotect_Then_Fails()
    {
        var protector = CreateProtector();
        var tenantId = Guid.NewGuid();
        var cursor = protector.Protect(new CursorToken(TaskItemSortMode.IdAsc, tenantId, string.Empty, Guid.CreateVersion7()));
        var tampered = cursor[..^2] + (cursor[^2] == 'A' ? 'B' : 'A') + cursor[^1];

        Assert.IsFalse(protector.TryUnprotect(tampered, TaskItemSortMode.IdAsc, tenantId, out _));
        Assert.IsFalse(protector.TryUnprotect("not-base64-url!!", TaskItemSortMode.IdAsc, tenantId, out _));
        Assert.IsFalse(protector.TryUnprotect(string.Empty, TaskItemSortMode.IdAsc, tenantId, out _));
    }

    /// <summary>
    /// Verifies a cursor from a different key ring is refused. This is the documented restart caveat:
    /// without a persisted ring, outstanding cursors stop decoding, and they must fail closed.
    /// </summary>
    [TestMethod]
    public void Given_DifferentKeyRing_When_Unprotect_Then_Fails()
    {
        var tenantId = Guid.NewGuid();
        var minted = CreateProtector().Protect(
            new CursorToken(TaskItemSortMode.IdAsc, tenantId, string.Empty, Guid.CreateVersion7()));

        var otherRing = new DataProtectionCursorProtector(DataProtectionProvider.Create("another-application"));

        Assert.IsFalse(otherRing.TryUnprotect(minted, TaskItemSortMode.IdAsc, tenantId, out _));
    }
}
