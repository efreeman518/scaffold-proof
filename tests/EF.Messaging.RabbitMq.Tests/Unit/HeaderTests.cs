using System.Text;

namespace EF.Messaging.RabbitMq.Tests.Unit;

/// <summary>
/// Contract 2 header type validation and contract 4 <c>x-death</c> counting. The broker encodes an <c>x-death</c>
/// header as a list of field tables whose <c>queue</c> is a UTF-8 byte array and whose <c>count</c> is a long.
/// Pure unit tier - no broker.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class HeaderTests
{
    private static Dictionary<string, object?> DeathEntry(string queue, long count, string reason = "rejected") => new()
    {
        ["queue"] = Encoding.UTF8.GetBytes(queue),
        ["count"] = count,
        ["reason"] = Encoding.UTF8.GetBytes(reason)
    };

    [TestMethod]
    public void Given_NoDeathHeader_When_Counted_Then_Zero() =>
        Assert.AreEqual(0, RabbitMqHeaders.DeathCount(new Dictionary<string, object?>(), "q"));

    [TestMethod]
    public void Given_DeathEntriesForThisQueue_When_Counted_Then_CountsAreSummed()
    {
        Dictionary<string, object?> headers = new()
        {
            ["x-death"] = new List<object?> { DeathEntry("q", 2), DeathEntry("q", 3) }
        };

        Assert.AreEqual(5, RabbitMqHeaders.DeathCount(headers, "q"));
    }

    [TestMethod]
    public void Given_DeathEntriesForAnotherQueue_When_Counted_Then_TheyAreIgnored()
    {
        Dictionary<string, object?> headers = new()
        {
            ["x-death"] = new List<object?> { DeathEntry("other", 7), DeathEntry("q", 1) }
        };

        Assert.AreEqual(1, RabbitMqHeaders.DeathCount(headers, "q"));
    }

    [TestMethod]
    public void Given_DeathHeaderOfUnexpectedShape_When_Counted_Then_Zero()
    {
        Dictionary<string, object?> headers = new() { ["x-death"] = "not-a-table-list" };

        Assert.AreEqual(0, RabbitMqHeaders.DeathCount(headers, "q"));
    }

    [TestMethod]
    public void Given_SupportedHeaderTypes_When_Validated_Then_NoThrow()
    {
        Dictionary<string, object?> headers = new()
        {
            ["s"] = "text",
            ["i"] = 1,
            ["l"] = 2L,
            ["b"] = true,
            ["bytes"] = new byte[] { 1, 2 },
            ["null"] = null
        };

        RabbitMqHeaders.Validate(headers);
    }

    [TestMethod]
    public void Given_UnsupportedHeaderType_When_Validated_Then_ArgumentException()
    {
        Dictionary<string, object?> headers = new() { ["when"] = DateTimeOffset.UnixEpoch };

        ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(() => RabbitMqHeaders.Validate(headers));

        Assert.Contains("when", exception.Message, StringComparison.Ordinal);
    }
}
