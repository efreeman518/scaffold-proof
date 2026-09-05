using Microsoft.Extensions.Logging;
using System.Threading.RateLimiting;
using TaskFlow.Observability.Meters;

namespace TaskFlow.Infrastructure.Caching.RateLimiting;

/// <summary>
/// Wraps a distributed limiter so an unreachable backend admits the request instead of rejecting it.
/// <para>
/// This is a deliberate, scoped failure policy, not a swallowed error: a limiter exists to protect the API
/// from load, and failing closed would turn a Redis blip into a total outage of a healthy API. The trade is
/// that while the backend is down the API is unlimited, so every occurrence increments
/// <c>taskflow.ratelimit.backend_failure</c> and logs a warning - that counter is the only signal that the
/// limit is not being enforced, and it is what an alert should watch.
/// </para>
/// </summary>
public sealed class FailOpenRateLimiter(
    RateLimiter inner, RateLimitingMeter meter, ILogger logger) : RateLimiter
{
    /// <inheritdoc />
    public override TimeSpan? IdleDuration => inner.IdleDuration;

    /// <inheritdoc />
    public override RateLimiterStatistics? GetStatistics() => inner.GetStatistics();

    /// <inheritdoc />
    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        try
        {
            return inner.AttemptAcquire(permitCount);
        }
        catch (Exception ex)
        {
            return Admit(ex);
        }
    }

    /// <inheritdoc />
    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken ct)
    {
        try
        {
            return await inner.AcquireAsync(permitCount, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The caller went away; that is not a backend failure and must not be counted as one.
            throw;
        }
        catch (Exception ex)
        {
            return Admit(ex);
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore() => await inner.DisposeAsync().ConfigureAwait(false);

    /// <summary>Records the backend failure and admits the request.</summary>
    private RateLimitLease Admit(Exception ex)
    {
        meter.RecordBackendFailure();
        logger.LogWarning(ex, "Rate limiter backend unavailable; admitting the request unmetered.");
        return AdmittedLease.Instance;
    }

    /// <summary>An acquired lease carrying no metadata, returned when the backend cannot answer.</summary>
    private sealed class AdmittedLease : RateLimitLease
    {
        public static readonly AdmittedLease Instance = new();

        public override bool IsAcquired => true;

        public override IEnumerable<string> MetadataNames => [];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            return false;
        }
    }
}
