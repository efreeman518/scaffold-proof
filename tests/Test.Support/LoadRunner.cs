using System.Collections.Concurrent;
using System.Diagnostics;

namespace Test.Support;

/// <summary>
/// Outcome of a <see cref="LoadRunner"/> run. Latency percentiles cover successful requests only; failed and
/// dropped requests count toward <see cref="ErrorRate"/>, and <see cref="FailureReasons"/> says why they failed.
/// </summary>
public sealed record LoadResult(
    int Offered, int Succeeded, int Failed, int Dropped, TimeSpan Elapsed,
    TimeSpan P50, TimeSpan P95, TimeSpan P99, TimeSpan Max, IReadOnlyDictionary<string, int> FailureReasons)
{
    /// <summary>Fraction of offered requests that failed or were dropped; zero when nothing was offered.</summary>
    public double ErrorRate => Offered == 0 ? 0 : (double)(Failed + Dropped) / Offered;

    /// <summary>Successful requests per second of wall-clock run time.</summary>
    public double Throughput => Elapsed <= TimeSpan.Zero ? 0 : Succeeded / Elapsed.TotalSeconds;

    /// <inheritdoc />
    public override string ToString() =>
        $"offered={Offered} succeeded={Succeeded} failed={Failed} dropped={Dropped} errorRate={ErrorRate:P2} " +
        $"throughput={Throughput:F1}/s p50={P50.TotalMilliseconds:F0}ms p95={P95.TotalMilliseconds:F0}ms " +
        $"p99={P99.TotalMilliseconds:F0}ms max={Max.TotalMilliseconds:F0}ms elapsed={Elapsed.TotalSeconds:F1}s" +
        (FailureReasons.Count == 0 ? "" : " failures=" + string.Join(", ", FailureReasons.Select(r => $"{r.Key}:{r.Value}")));
}

/// <summary>
/// In-house open-model load runner. No commercial-license load package is needed for the shape of load test
/// this repo asserts on: fixed-rate scheduling, percentile latency, and a bounded-concurrency drop count.
/// </summary>
public static class LoadRunner
{
    /// <summary>Failure reason recorded when the operation returns <see langword="false"/>.</summary>
    public const string UnsuccessfulResult = "unsuccessful-result";

    // Open model: requests start on a fixed schedule so a slow server cannot throttle the offered load,
    // and latency is measured from the scheduled start (no coordinated omission). A request that finds
    // maxInFlight exhausted is dropped and counted as an error - the system did not keep up.
    // Task.Delay resolution is about 15.6 ms on Windows, so above roughly 60 requests per second the loop
    // releases requests in small bursts; latency stays measured from each request's scheduled start.
    public static async Task<LoadResult> RunAsync(
        Func<CancellationToken, Task<bool>> operation, int ratePerSecond, TimeSpan duration,
        int maxInFlight, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ratePerSecond);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxInFlight);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);

        // Rounded, not truncated: 0.29 s at 100/s is 29 requests, not the 28 a truncated double product gives.
        // A run that offers nothing proves nothing.
        var product = Math.Round(ratePerSecond * duration.TotalSeconds, MidpointRounding.AwayFromZero);
        if (product < 1 || product > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration,
                $"{ratePerSecond}/s for {duration} offers {product} requests; it must offer between 1 and {int.MaxValue}.");
        }

        var offered = (int)product;
        var interval = TimeSpan.FromSeconds(1.0 / ratePerSecond);
        var latencies = new TimeSpan?[offered];
        var reasons = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var failed = 0;
        var dropped = 0;
        using var gate = new SemaphoreSlim(maxInFlight);
        var inFlight = new List<Task>(offered);
        var clock = Stopwatch.StartNew();

        try
        {
            for (var i = 0; i < offered; i++)
            {
                // A run behind schedule never reaches the delay, so cancellation is checked on every iteration.
                ct.ThrowIfCancellationRequested();
                var scheduled = interval * i;
                var wait = scheduled - clock.Elapsed;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                if (!gate.Wait(0)) { dropped++; continue; }
                inFlight.Add(RunOneAsync(i, scheduled));
            }
        }
        finally
        {
            // Every started request settles before the gate is disposed, including when the run is cancelled.
            await Task.WhenAll(inFlight);
        }

        ct.ThrowIfCancellationRequested();
        var elapsed = clock.Elapsed;
        var sorted = latencies.OfType<TimeSpan>().Order().ToArray();
        return new LoadResult(offered, sorted.Length, failed, dropped, elapsed,
            Percentile(sorted, 0.50), Percentile(sorted, 0.95), Percentile(sorted, 0.99),
            sorted.Length == 0 ? TimeSpan.Zero : sorted[^1], reasons);

        async Task RunOneAsync(int index, TimeSpan scheduled)
        {
            // Leave the scheduling loop before the operation's synchronous part runs.
            await Task.Yield();
            try
            {
                if (await operation(ct))
                {
                    latencies[index] = clock.Elapsed - scheduled;
                }
                else
                {
                    Fail(UnsuccessfulResult);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The run itself was cancelled; RunAsync rethrows after every request settles.
            }
            catch (Exception ex)
            {
                // Any exception is a failed request, never an aborted run; its type is kept as the reason.
                Fail(ex.GetType().Name);
            }
            finally
            {
                gate.Release();
            }
        }

        void Fail(string reason)
        {
            Interlocked.Increment(ref failed);
            reasons.AddOrUpdate(reason, 1, static (_, count) => count + 1);
        }
    }

    // Nearest-rank percentile over an ascending sample; an empty sample reports zero and the error-rate
    // assertion fails instead.
    public static TimeSpan Percentile(IReadOnlyList<TimeSpan> sorted, double p)
    {
        ArgumentNullException.ThrowIfNull(sorted);
        if (!(p > 0 && p <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(p), p, "Percentile must be greater than 0 and at most 1.");
        }

        return sorted.Count == 0 ? TimeSpan.Zero : sorted[Math.Max(0, (int)Math.Ceiling(p * sorted.Count) - 1)];
    }
}
