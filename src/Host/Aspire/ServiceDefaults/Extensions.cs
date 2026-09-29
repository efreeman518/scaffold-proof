using EF.AspNetCore.Correlation;
using EF.AspNetCore.HealthChecks;
using EF.Host;
using EF.OpenTelemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using TaskFlow.Observability.Meters;

namespace Microsoft.Extensions.Hosting;

/// <summary>Configures extensions host behavior for TaskFlow runtime services.</summary>
public static class Extensions
{
    /// <summary>
    /// <c>EF.Messaging.RabbitMq.RabbitMqMetrics.MeterName</c> (publish, confirm, consume and dead-letter
    /// instruments), spelled out so ServiceDefaults - which every host, the Gateway and UI hosts included,
    /// references - does not take a RabbitMQ client dependency for one string. A unit test probes the export
    /// with the package constant, so a rename there fails the test.
    /// </summary>
    private const string RabbitMqMeterName = "EF.Messaging.RabbitMq";

    /// <summary>
    /// <c>EF.Messaging.MessagingMetrics.MeterName</c> (outbox, work-table, inbox and consumer instruments) and
    /// <c>EF.Messaging.Tracing.MessagingActivitySource.Name</c> (broker send and process spans) - the same string,
    /// spelled out for the same reason as <see cref="RabbitMqMeterName"/>; the unit test probes both constants.
    /// </summary>
    private const string MessagingName = "EF.Messaging";

    /// <summary>
    /// <c>EF.Data.Outbox.OutboxActivitySource.Name</c> (one drain span per non-empty work-table claim), spelled out
    /// so ServiceDefaults does not take an EF Core dependency for one string; the unit test probes the constant.
    /// </summary>
    private const string OutboxActivitySourceName = "EF.Data.Outbox";

    /// <summary>
    /// <c>EF.RateLimiting.RateLimitingTelemetryOptions.MeterName</c> (<c>ratelimit.rejected</c>,
    /// <c>ratelimit.backend_failure</c> - the fail-open signal to alert on), spelled out for the same reason as
    /// <see cref="RabbitMqMeterName"/>; the unit test probes the package default.
    /// </summary>
    private const string RateLimitingMeterName = "EF.RateLimiting";

    /// <summary>Registers service defaults dependencies in the service container.</summary>
    public static IHostApplicationBuilder AddServiceDefaults(this IHostApplicationBuilder builder)
    {
        builder.ConfigureOpenTelemetry();
        builder.AddDefaultHealthChecks();
        builder.AddHostLifecycle();

        builder.Services.AddServiceDiscovery();
        // Outbound calls carry the inbound correlation id (HttpContext.TraceIdentifier, set by UseCorrelationId);
        // outside a request (message consumers, jobs, a Blazor circuit) the handler sends nothing and never throws.
        builder.Services.AddCorrelationId();
        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddCorrelationIdPropagation();
            // D-063: the standard handler retries every method by default, so a 5xx or timeout on a POST would
            // repeat a non-idempotent write. Safe methods keep their retries.
            http.AddStandardResilienceHandler(o => o.Retry.DisableForUnsafeHttpMethods());
            http.AddServiceDiscovery();
        });

        return builder;
    }

    /// <summary>
    /// Logs, traces and metrics through <c>EF.OpenTelemetry</c> (section <c>OpenTelemetry</c>: <c>MetricsEnabled</c>,
    /// <c>Tracing:SampleRatio</c> (D-065), <c>SuppressAspNetCoreInstrumentation</c>, which Functions sets because its
    /// host already reports each invocation). TaskFlow's own meters and sources are named once here rather than per
    /// host (D-053): a meter or source added to a shared library is then exported by every host that uses it.
    /// </summary>
    public static IHostApplicationBuilder ConfigureOpenTelemetry(this IHostApplicationBuilder builder) =>
        builder.AddEfOpenTelemetry(o =>
        {
            o.MeterNames.AddRange([
                RateLimitingMeterName,
                StreamingMeter.MeterName,
                MessagingName,
                RabbitMqMeterName]);
            o.ActivitySourceNames.AddRange([
                MessagingName,
                OutboxActivitySourceName]);
        });

    /// <summary>Registers the always-healthy <c>self</c> liveness check (tag <c>live</c>).</summary>
    public static IHostApplicationBuilder AddDefaultHealthChecks(this IHostApplicationBuilder builder)
    {
        builder.Services.AddHealthChecks().AddSelfCheck();

        return builder;
    }

    /// <summary>
    /// Maps the D-049 probe contract, identical on every host, anonymous and exempt from rate limiting:
    /// <list type="bullet">
    /// <item><c>/healthz/live</c> - tag <c>live</c> only (<c>self</c>). A liveness failure means restart the
    /// process, so it must never depend on anything a restart cannot fix.</item>
    /// <item><c>/healthz/ready</c> - tag <c>ready</c>: the dependencies an instance needs before it should be
    /// routed traffic (database, outbox, scheduler, broker on consumer hosts). The cache is deliberately not
    /// tagged <c>ready</c>: it degrades to L1 rather than failing requests.</item>
    /// <item><c>/healthz</c> - every registered check, for humans and Compose healthchecks.</item>
    /// </list>
    /// </summary>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapEfHealthEndpoints();

        return app;
    }
}
