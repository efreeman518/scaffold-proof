using TaskFlow.Domain.Shared;

namespace Test.Unit.Domain;

/// <summary>
/// Validates the UUIDv5 helper the scheduler jobs key their outbox rows and recurrence occurrences on.
/// The golden value below is the contract: it is computed from the RFC 4122 algorithm independently of this
/// code, so a change to the namespace, the separator, or the byte order fails here rather than silently
/// producing a second copy of every event in production.
/// Pure-unit tier.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class DeterministicGuidTests
{
    /// <summary>SHA-1 of the TaskFlow namespace bytes plus "test|a|b", stamped with version 5 and the RFC variant.</summary>
    private const string GoldenValue = "8e4821f7-cf86-5742-ab11-a14ad12f8b96";

    /// <summary>The value matches an independently computed UUIDv5, byte order and all.</summary>
    [TestMethod]
    public void Create_MatchesTheRfc4122Value()
    {
        Assert.AreEqual(Guid.Parse(GoldenValue), DeterministicGuid.Create("test", "a", "b"));
    }

    /// <summary>Same inputs, same value - the property every idempotent producer depends on.</summary>
    [TestMethod]
    public void Create_IsStableAcrossCalls()
    {
        var first = DeterministicGuid.Create("overdue", "tenant-1", "task-1", "2026-09-04T00:00:00.0000000Z");
        var second = DeterministicGuid.Create("overdue", "tenant-1", "task-1", "2026-09-04T00:00:00.0000000Z");

        Assert.AreEqual(first, second);
    }

    /// <summary>The version nibble is 5 and the variant is RFC 4122, so the value is a well-formed name-based UUID.</summary>
    [TestMethod]
    public void Create_SetsVersionAndVariantBits()
    {
        var bytes = DeterministicGuid.Create("overdue", "tenant-1").ToByteArray(bigEndian: true);

        Assert.AreEqual(0x50, bytes[6] & 0xF0, "version nibble must be 5");
        Assert.AreEqual(0x80, bytes[8] & 0xC0, "variant must be RFC 4122");
    }

    /// <summary>Different namespaces and different parts produce different values, including at part boundaries.</summary>
    [TestMethod]
    public void Create_DistinguishesNamespacesAndPartBoundaries()
    {
        Assert.AreNotEqual(
            DeterministicGuid.Create("overdue", "t", "1"),
            DeterministicGuid.Create("recurrence", "t", "1"));

        // The separator is what stops ("a","bc") and ("ab","c") from colliding.
        Assert.AreNotEqual(
            DeterministicGuid.Create("ns", "a", "bc"),
            DeterministicGuid.Create("ns", "ab", "c"));
    }
}
