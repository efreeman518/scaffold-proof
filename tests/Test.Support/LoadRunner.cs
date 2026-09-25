using System.Diagnostics;

namespace Test.Support;

/// <summary>
/// Outcome of a <see cref="LoadRunner"/> run: request counts and latency percentiles for a fixed-schedule
/// load run.
/// </summary>
public sealed record LoadResult(int Offered, int Failed, int Dropped, TimeSpan P50, TimeSpan P95, TimeSpan P99)
{
    /// <summary>Fraction of offered requests that failed or were dropped; zero when nothing was offered.</summary>
    public double ErrorRate => Offered == 0 ? 0 : (double)(Failed + Dropped) / Offered;
}

/// <summary>
/// In-house open-model load runner. No commercial-license load package is needed for the shape of load test
/// this repo asserts on: fixed-rate scheduling, percentile latency, and a bounded-concurrency drop count.
/// </summary>
public static class LoadRunner
{
    // Open model: requests start on a fixed schedule so a slow server cannot throttle the offered load,
    // and latency is measured from the scheduled start (no coordinated omission). A request that finds
    // maxInFlight exhausted is dropped and counted as an error - the system did not keep up.
    public static async Task<LoadResult> RunAsync(
        Func<CancellationToken, Task<bool>> operation, int ratePerSecond, TimeSpan duration,
        int maxInFlight, CancellationToken ct)
    {
        var offered = (int)(ratePerSecond * duration.TotalSeconds);
        var interval = TimeSpan.FromSeconds(1.0 / ratePerSecond);
        var latencies = new TimeSpan?[offered];
        var failed = 0;
        var dropped = 0;
        using var gate = new SemaphoreSlim(maxInFlight);
        var inFlight = new List<Task>(offered);
        var clock = Stopwatch.StartNew();

        for (var i = 0; i < offered; i++)
        {
            var scheduled = interval * i;
            var wait = scheduled - clock.Elapsed;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            if (!gate.Wait(0)) { dropped++; continue; }

            var index = i;
            inFlight.Add(Task.Run(async () =>
            {
                try
                {
                    if (!await operation(ct)) Interlocked.Increment(ref failed);
                }
                catch (Exception ex) when (ex is HttpRequestException
                    || (ex is TaskCanceledException && !ct.IsCancellationRequested)) // client timeout
                {
                    Interlocked.Increment(ref failed);
                }
                finally
                {
                    latencies[index] = clock.Elapsed - scheduled;
                    gate.Release();
                }
            }, ct));
        }

        await Task.WhenAll(inFlight);
        var sorted = latencies.OfType<TimeSpan>().Order().ToArray();
        return new LoadResult(offered, failed, dropped,
            Percentile(sorted, 0.50), Percentile(sorted, 0.95), Percentile(sorted, 0.99));
    }

    // Nearest-rank percentile; an empty sample reports zero and the error-rate assertion fails instead.
    public static TimeSpan Percentile(TimeSpan[] sorted, double p) =>
        sorted.Length == 0 ? TimeSpan.Zero : sorted[Math.Max(0, (int)Math.Ceiling(p * sorted.Length) - 1)];
}
