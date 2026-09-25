using Test.Support;

namespace Test.Unit.Load;

/// <summary>
/// Guards the in-house <see cref="LoadRunner"/> itself: a broken percentile calculation, a runner that
/// swallows the offered-request contract, or a saturation path that stops dropping would silently pass
/// every downstream load gate in <c>Test.Load</c>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class LoadRunnerTests
{
    /// <summary>MSTest-injected context; supplies the per-test cancellation token.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>Nearest-rank percentile over a known 1..100 ms sample lands on the exact millisecond.</summary>
    [TestMethod]
    public void Percentile_Over1To100Milliseconds_ReturnsExactRank()
    {
        var sorted = Enumerable.Range(1, 100).Select(ms => TimeSpan.FromMilliseconds(ms)).ToArray();

        Assert.AreEqual(TimeSpan.FromMilliseconds(50), LoadRunner.Percentile(sorted, 0.50));
        Assert.AreEqual(TimeSpan.FromMilliseconds(95), LoadRunner.Percentile(sorted, 0.95));
    }

    /// <summary>An empty sample reports zero instead of throwing, so a broken run fails on error rate.</summary>
    [TestMethod]
    public void Percentile_OverEmptySample_ReturnsZero()
    {
        Assert.AreEqual(TimeSpan.Zero, LoadRunner.Percentile([], 0.95));
    }

    /// <summary>An operation that always throws HttpRequestException counts every offered request as failed and never
    /// propagates the exception out of the runner.</summary>
    [TestMethod]
    public async Task RunAsync_WhenOperationThrowsHttpRequestException_CountsEveryRequestAsFailed()
    {
        var result = await LoadRunner.RunAsync(
            _ => throw new HttpRequestException("simulated failure"),
            ratePerSecond: 50, duration: TimeSpan.FromSeconds(0.2), maxInFlight: 100, TestContext.CancellationToken);

        Assert.AreEqual(result.Offered, result.Failed);
        Assert.AreEqual(0, result.Dropped);
    }

    /// <summary>An operation slower than maxInFlight allows drops most of the offered load: the system did not
    /// keep up, and the drop plus error-rate accounting must reflect that.</summary>
    [TestMethod]
    public async Task RunAsync_WhenConcurrencyIsSaturated_DropsMostRequests()
    {
        var result = await LoadRunner.RunAsync(
            async ct => { await Task.Delay(TimeSpan.FromSeconds(1), ct); return true; },
            ratePerSecond: 100, duration: TimeSpan.FromSeconds(0.1), maxInFlight: 1, TestContext.CancellationToken);

        Assert.IsGreaterThanOrEqualTo(8, result.Dropped);
        Assert.IsGreaterThanOrEqualTo(0.8, result.ErrorRate);
    }
}
