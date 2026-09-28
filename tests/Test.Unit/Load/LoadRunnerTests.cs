using Test.Support;

namespace Test.Unit.Load;

/// <summary>
/// Guards the in-house <see cref="LoadRunner"/> itself: a broken percentile calculation, a runner that
/// swallows the offered-request contract, a saturation path that stops dropping, or an exception that aborts
/// the run would silently break every downstream load gate in <c>Test.Load</c>.
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
        Assert.AreEqual(0, result.Succeeded);
        Assert.AreEqual(result.Offered, result.FailureReasons[nameof(HttpRequestException)]);
    }

    /// <summary>An unexpected exception type is a failed request with its type as the reason, not an aborted run.</summary>
    [TestMethod]
    public async Task RunAsync_WhenOperationThrowsUnexpectedException_CountsItAsFailed()
    {
        var result = await LoadRunner.RunAsync(
            _ => throw new InvalidOperationException("simulated defect"),
            ratePerSecond: 50, duration: TimeSpan.FromSeconds(0.1), maxInFlight: 100, TestContext.CancellationToken);

        Assert.AreEqual(5, result.Failed);
        Assert.AreEqual(5, result.FailureReasons[nameof(InvalidOperationException)]);
    }

    /// <summary>Percentiles cover successes only: slow failures do not inflate latency, and a false result is a
    /// named failure reason.</summary>
    [TestMethod]
    public async Task RunAsync_WithMixedOutcomes_MeasuresLatencyOverSuccessesOnly()
    {
        var calls = 0;
        var result = await LoadRunner.RunAsync(
            async ct =>
            {
                if (Interlocked.Increment(ref calls) % 2 == 0) return true;
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
                return false;
            },
            ratePerSecond: 100, duration: TimeSpan.FromSeconds(0.1), maxInFlight: 100, TestContext.CancellationToken);

        Assert.AreEqual(5, result.Succeeded);
        Assert.AreEqual(5, result.FailureReasons[LoadRunner.UnsuccessfulResult]);
        Assert.IsLessThan(TimeSpan.FromMilliseconds(400), result.Max, result.ToString());
        Assert.IsGreaterThan(0.0, result.Throughput);
    }

    /// <summary>The offered count rounds the rate-duration product instead of truncating it.</summary>
    [TestMethod]
    public async Task RunAsync_OfferedCount_RoundsRateTimesDuration()
    {
        var result = await LoadRunner.RunAsync(
            _ => Task.FromResult(true),
            ratePerSecond: 100, duration: TimeSpan.FromSeconds(0.29), maxInFlight: 100, TestContext.CancellationToken);

        Assert.AreEqual(29, result.Offered);
        Assert.AreEqual(29, result.Succeeded);
    }

    /// <summary>Cancelling a run surfaces as cancellation once every started request has settled.</summary>
    [TestMethod]
    public async Task RunAsync_WhenCancelled_ThrowsOperationCanceled()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<OperationCanceledException>(() => LoadRunner.RunAsync(
            async ct => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return true; },
            ratePerSecond: 20, duration: TimeSpan.FromSeconds(10), maxInFlight: 100, cts.Token));
    }

    /// <summary>A token cancelled before the run starts throws without invoking the operation.</summary>
    [TestMethod]
    public async Task RunAsync_WhenAlreadyCancelled_ThrowsWithoutInvokingOperation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var invocations = 0;

        await Assert.ThrowsAsync<OperationCanceledException>(() => LoadRunner.RunAsync(
            _ => { Interlocked.Increment(ref invocations); return Task.FromResult(true); },
            ratePerSecond: 100, duration: TimeSpan.FromSeconds(1), maxInFlight: 10, cts.Token));

        Assert.AreEqual(0, invocations);
    }

    /// <summary>A percentile outside (0, 1] is rejected instead of indexing out of the sample.</summary>
    [TestMethod]
    [DataRow(0.0)]
    [DataRow(1.1)]
    public void Percentile_OutOfRange_Throws(double p)
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => LoadRunner.Percentile([TimeSpan.FromMilliseconds(1)], p));
    }

    /// <summary>A non-positive rate, concurrency, or duration, or a run that rounds to zero requests, is rejected
    /// before any request is sent.</summary>
    [TestMethod]
    [DataRow(0, 1, 1.0)]
    [DataRow(1, 0, 1.0)]
    [DataRow(1, 1, 0.0)]
    [DataRow(100, 1, 0.004)]
    public async Task RunAsync_WithNonPositiveArguments_Throws(int ratePerSecond, int maxInFlight, double seconds)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => LoadRunner.RunAsync(
            _ => Task.FromResult(true), ratePerSecond, TimeSpan.FromSeconds(seconds), maxInFlight,
            TestContext.CancellationToken));
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
