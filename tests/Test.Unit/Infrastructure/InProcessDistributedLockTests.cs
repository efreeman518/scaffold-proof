using EF.Common;

namespace Test.Unit.Infrastructure;

/// <summary>
/// D-052 in-process fallback: exactly one contender holds a key at a time, and releasing hands it to the
/// next. Per key matters as much as the exclusion - a single global gate would serialize unrelated startup
/// tasks behind each other.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class InProcessDistributedLockTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    /// <summary>MSTest-injected context; supplies the per-test cancellation token.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>Two contenders on one key: the first wins, the second wins only after release.</summary>
    [TestMethod]
    public async Task TryAcquireAsync_TwoContendersOnOneKey_SecondWinsAfterRelease()
    {
        var sut = new InProcessDistributedLock();

        var first = await sut.TryAcquireAsync("taskflow:provision", Ttl, TestContext.CancellationToken);
        Assert.IsNotNull(first);

        var contended = await sut.TryAcquireAsync("taskflow:provision", Ttl, TestContext.CancellationToken);
        Assert.IsNull(contended, "the second contender must not hold the same key");

        await first.DisposeAsync();

        var afterRelease = await sut.TryAcquireAsync("taskflow:provision", Ttl, TestContext.CancellationToken);
        Assert.IsNotNull(afterRelease, "release must hand the key to the next contender");
        await afterRelease.DisposeAsync();
    }

    /// <summary>Different keys are independent gates.</summary>
    [TestMethod]
    public async Task TryAcquireAsync_DifferentKeys_DoNotContend()
    {
        var sut = new InProcessDistributedLock();

        var provision = await sut.TryAcquireAsync("taskflow:provision", Ttl, TestContext.CancellationToken);
        var topology = await sut.TryAcquireAsync("taskflow:rabbitmq-topology", Ttl, TestContext.CancellationToken);

        Assert.IsNotNull(provision);
        Assert.IsNotNull(topology);

        await provision.DisposeAsync();
        await topology.DisposeAsync();
    }
}
