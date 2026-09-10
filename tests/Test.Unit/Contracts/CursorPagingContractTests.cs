using EF.Common.Contracts;
using EF.Data.Contracts;
using TaskFlow.Application.Models.Paging;
using TaskFlow.Infrastructure.Repositories;
using Test.Support;

namespace Test.Unit.Contracts;

/// <summary>
/// Unit coverage for the cursor primitives (GR-18): page-size limits and the codec's rejection paths. A
/// cursor that decodes when it should not is a cross-tenant read; a cursor that fails to round-trip is a
/// broken page-through - both are cheapest to catch here.
/// The primitives themselves are package types now (<c>EF.Common.Contracts.PageSizeLimits</c>,
/// <c>EF.Data.Contracts.CursorCodec</c>, package requests 21 and 27); what stays TaskFlow's own, and is
/// what these tests pin, is the scope key the token is bound to:
/// <c>TaskItemRepositoryQuery.CursorScope</c> folds the sort mode in beside the tenant, because the
/// codec's payload has no sort-mode field and a cursor minted under one ordering names nothing in another.
/// Pure-unit tier: no host, no database.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class CursorPagingContractTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static CursorCodec Codec => TestCursorCodec.Instance;

    private static string Scope(TaskItemSortMode sortMode) =>
        TaskItemRepositoryQuery.CursorScope(TenantId, sortMode);

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

    /// <summary>Verifies a request with no body-supplied page size still carries the app default.</summary>
    [TestMethod]
    public void Given_DefaultedRequest_When_Constructed_Then_CarriesTheDefaultPageSize() =>
        Assert.AreEqual(PageSizeLimits.Default, new TaskItemCursorSearchRequest().PageSize);

    /// <summary>Verifies a cursor round-trips through the codec unchanged.</summary>
    [TestMethod]
    public void Given_Position_When_EncodedAndDecoded_Then_RoundTrips()
    {
        var position = new CursorPosition("2026-06-01T12:00:00", Guid.CreateVersion7());

        var cursor = Codec.Encode(Scope(TaskItemSortMode.DueDateAsc), position);

        Assert.AreEqual(position, Codec.Decode(cursor, Scope(TaskItemSortMode.DueDateAsc)));
    }

    /// <summary>Verifies a null sort key (the last-sorting DueDate arm) round-trips as null.</summary>
    [TestMethod]
    public void Given_NullSortKey_When_EncodedAndDecoded_Then_StaysNull()
    {
        var cursor = Codec.Encode(Scope(TaskItemSortMode.DueDateAsc), new CursorPosition(null, Guid.CreateVersion7()));

        Assert.IsNull(Codec.Decode(cursor, Scope(TaskItemSortMode.DueDateAsc)).SortKey);
    }

    /// <summary>Verifies a cursor minted for one tenant cannot be replayed by another.</summary>
    [TestMethod]
    public void Given_ForeignTenant_When_Decoded_Then_Fails()
    {
        var cursor = Codec.Encode(
            TaskItemRepositoryQuery.CursorScope(Guid.NewGuid(), TaskItemSortMode.IdAsc),
            new CursorPosition(null, Guid.CreateVersion7()));

        Assert.IsFalse(Codec.TryDecode(cursor, Scope(TaskItemSortMode.IdAsc), out _));
    }

    /// <summary>Verifies a cursor is bound to the sort mode it was minted under.</summary>
    [TestMethod]
    public void Given_DifferentSortMode_When_Decoded_Then_Fails()
    {
        var cursor = Codec.Encode(Scope(TaskItemSortMode.IdAsc), new CursorPosition(null, Guid.CreateVersion7()));

        Assert.IsFalse(Codec.TryDecode(cursor, Scope(TaskItemSortMode.ModifiedDesc), out _));
    }

    /// <summary>Verifies tampering is detected rather than producing a plausible-looking position.</summary>
    [TestMethod]
    public void Given_TamperedCursor_When_Decoded_Then_Fails()
    {
        var scope = Scope(TaskItemSortMode.IdAsc);
        var cursor = Codec.Encode(scope, new CursorPosition(null, Guid.CreateVersion7()));
        var tampered = cursor[..^2] + (cursor[^2] == 'A' ? 'B' : 'A') + cursor[^1];

        Assert.IsFalse(Codec.TryDecode(tampered, scope, out _));
        Assert.IsFalse(Codec.TryDecode("not-base64-url!!", scope, out _));
        Assert.IsFalse(Codec.TryDecode(string.Empty, scope, out _));
        Assert.ThrowsExactly<ArgumentException>(() => Codec.Decode(tampered, scope));
    }

    /// <summary>
    /// Verifies a cursor signed with another key is refused. This is the rotation caveat: rotating the
    /// column-encryption DEK the signing key is derived from invalidates outstanding cursors, and they
    /// must fail closed rather than decode into a position in someone else's ordering.
    /// </summary>
    [TestMethod]
    public void Given_DifferentSigningKey_When_Decoded_Then_Fails()
    {
        var scope = Scope(TaskItemSortMode.IdAsc);
        var minted = Codec.Encode(scope, new CursorPosition(null, Guid.CreateVersion7()));

        var otherKey = new CursorCodec(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

        Assert.IsFalse(otherKey.TryDecode(minted, scope, out _));
    }
}
