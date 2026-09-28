using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// GET/HEAD-only request hedging (D-051). Opt-in per client: this is applied to the Blazor read pipeline, where a
/// tail-latency read stalls a rendered page, and to nothing else.
/// </summary>
public static class ReadHedgingExtensions
{
    /// <summary>Configuration section the hedging knobs bind from.</summary>
    public const string ConfigSectionName = "Resilience:Hedging";

    /// <summary>
    /// Replaces the client's resilience pipeline with the standard hedging pipeline, narrowed to reads.
    /// <para>
    /// The standard hedging handler, not a plain <c>AddResilienceHandler(...).AddHedging(...)</c>: only the
    /// standard pipeline snapshots the request and hands every hedged attempt its own clone. A plain hedging
    /// strategy sends the same <see cref="HttpRequestMessage"/> from two attempts concurrently, and the header
    /// mutations each send performs (trace-context injection) race on it.
    /// </para>
    /// <para>
    /// Hedging never applies to a write, and that takes two guards rather than one because Polly can start a
    /// hedged attempt for two different reasons. <c>ShouldHandle</c> covers the outcome-triggered case: a
    /// transient failure on a GET or HEAD hedges, anything else does not. <c>DelayGenerator</c> covers the
    /// latency-triggered case, which consults no outcome at all - returning an infinite delay for a write is
    /// what stops the timer from ever spawning a second POST.
    /// </para>
    /// Apply it to a dedicated read client: it removes every resilience handler the client already has (the
    /// ServiceDefaults standard handler included), so attempt timeout, circuit breaker and total timeout come
    /// from <see cref="HttpStandardHedgingResilienceOptions"/> defaults, and GET retries become hedges.
    /// </summary>
    /// <param name="builder">The client to hedge reads for.</param>
    /// <param name="config">Configuration root carrying <see cref="ConfigSectionName"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <c>DelayMs</c> is negative, or <c>MaxHedgedAttempts</c> is outside 1..10 (Polly's range).
    /// </exception>
    public static IHttpClientBuilder AddReadHedging(this IHttpClientBuilder builder, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(config);

        var section = config.GetSection(ConfigSectionName);
        if (!section.GetValue("Enabled", true))
        {
            return builder;
        }

        var delayMs = section.GetValue("DelayMs", 250);
        var maxHedgedAttempts = section.GetValue("MaxHedgedAttempts", 1);
        ArgumentOutOfRangeException.ThrowIfNegative(delayMs, $"{ConfigSectionName}:DelayMs");
        ArgumentOutOfRangeException.ThrowIfLessThan(maxHedgedAttempts, 1, $"{ConfigSectionName}:MaxHedgedAttempts");
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxHedgedAttempts, 10, $"{ConfigSectionName}:MaxHedgedAttempts");
        var delay = TimeSpan.FromMilliseconds(delayMs);

        // Microsoft.Extensions.Http.Resilience marks RemoveAllResilienceHandlers experimental; it is the only way
        // to take the ServiceDefaults standard handler off one client. Remove this suppression when the
        // attribute is dropped from the API.
#pragma warning disable EXTEXP0001
        builder.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

        builder.AddStandardHedgingHandler().Configure(options =>
        {
            var hedging = options.Hedging;
            hedging.MaxHedgedAttempts = maxHedgedAttempts;
            hedging.Delay = delay;

            // Keep the package's own transient-outcome predicate and narrow it to reads, rather than
            // restating which status codes count as transient.
            var isTransient = hedging.ShouldHandle;
            hedging.ShouldHandle = args => IsRead(args.Context)
                ? isTransient(args)
                : ValueTask.FromResult(false);
            hedging.DelayGenerator = args => ValueTask.FromResult(
                IsRead(args.Context) ? delay : Timeout.InfiniteTimeSpan);
        });

        return builder;
    }

    private static bool IsRead(ResilienceContext context) =>
        context.GetRequestMessage()?.Method is { } method
        && (method == HttpMethod.Get || method == HttpMethod.Head);
}
