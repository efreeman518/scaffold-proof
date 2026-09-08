using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// GET-only request hedging (D-051). Opt-in per client: this is applied to the Blazor read pipeline, where a
/// tail-latency read stalls a rendered page, and to nothing else.
/// </summary>
public static class ReadHedgingExtensions
{
    /// <summary>Configuration section the hedging knobs bind from.</summary>
    public const string ConfigSectionName = "Resilience:Hedging";

    /// <summary>
    /// Adds a hedging handler that issues one parallel attempt when the first is slow or fails transiently.
    /// <para>
    /// Hedging never applies to a write, and that takes two guards rather than one because Polly can start a
    /// hedged attempt for two different reasons. <c>ShouldHandle</c> covers the outcome-triggered case: a
    /// transient failure on a GET hedges, anything on a non-GET does not. <c>DelayGenerator</c> covers the
    /// latency-triggered case, which consults no outcome at all - returning an infinite delay for a non-GET is
    /// what stops the timer from ever spawning a second POST. Restricting only <c>ShouldHandle</c> would leave
    /// a slow POST duplicated, which for a non-idempotent write means a duplicated side effect.
    /// </para>
    /// Added after the standard resilience handler on purpose, so it sits inside it: the standard pipeline's
    /// retry then wraps one hedged pair per attempt instead of multiplying attempts by hedges.
    /// </summary>
    /// <param name="builder">The client to hedge reads for.</param>
    /// <param name="config">Configuration root carrying <see cref="ConfigSectionName"/>.</param>
    public static IHttpClientBuilder AddReadHedging(this IHttpClientBuilder builder, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(config);

        var section = config.GetSection(ConfigSectionName);
        if (!section.GetValue("Enabled", true))
        {
            return builder;
        }

        var delay = TimeSpan.FromMilliseconds(section.GetValue("DelayMs", 250));
        var maxHedgedAttempts = section.GetValue("MaxHedgedAttempts", 1);

        builder.AddResilienceHandler("read-hedging", pipeline =>
        {
            var options = new HttpHedgingStrategyOptions
            {
                MaxHedgedAttempts = maxHedgedAttempts,
                Delay = delay
            };

            // Keep the package's own transient-outcome predicate and narrow it to reads, rather than
            // restating which status codes count as transient.
            var isTransient = options.ShouldHandle;
            options.ShouldHandle = args => IsRead(args.Context)
                ? isTransient(args)
                : ValueTask.FromResult(false);
            options.DelayGenerator = args => ValueTask.FromResult(
                IsRead(args.Context) ? delay : Timeout.InfiniteTimeSpan);

            pipeline.AddHedging(options);
        });

        return builder;
    }

    private static bool IsRead(ResilienceContext context) =>
        context.GetRequestMessage()?.Method == HttpMethod.Get;
}
