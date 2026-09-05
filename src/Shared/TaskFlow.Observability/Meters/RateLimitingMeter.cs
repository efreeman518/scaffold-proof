using System.Diagnostics.Metrics;

namespace TaskFlow.Observability.Meters;

/// <summary>
/// Rate-limiter instruments. Rejections by tier answer "is this tenant's plan too small or is it misbehaving";
/// backend failures answer the more urgent question, because the limiter fails open - while Redis is
/// unreachable the API is effectively unlimited, and this counter is the only signal that it is.
/// </summary>
public sealed class RateLimitingMeter
{
    public const string MeterName = "TaskFlow.RateLimiting";

    private readonly Meter _meter = new(MeterName, "1.0.0");
    private readonly Counter<long> _rejected;
    private readonly Counter<long> _backendFailure;

    /// <summary>Initializes rate limiting meter with required dependencies and default state.</summary>
    public RateLimitingMeter()
    {
        _rejected = _meter.CreateCounter<long>(
            "taskflow.ratelimit.rejected", "request", "Requests rejected by the per-tenant limiter, by tier.");
        _backendFailure = _meter.CreateCounter<long>(
            "taskflow.ratelimit.backend_failure", "failure",
            "Limiter backend failures. Each one is a request admitted without being counted.");
    }

    /// <summary>Records one rejected request for a tenant on <paramref name="tier"/>.</summary>
    public void RecordRejected(string tier) =>
        _rejected.Add(1, new KeyValuePair<string, object?>("tenant.tier", tier));

    /// <summary>Records one request admitted because the limiter backend could not be reached.</summary>
    public void RecordBackendFailure() => _backendFailure.Add(1);
}
