using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using TaskFlow.Observability.Meters;
using TaskFlow.Observability.Tracing;

namespace Microsoft.Extensions.Hosting;

/// <summary>Configures extensions host behavior for TaskFlow runtime services.</summary>
public static class Extensions
{
    /// <summary>Registers service defaults dependencies in the service container.</summary>
    public static IHostApplicationBuilder AddServiceDefaults(
        this IHostApplicationBuilder builder,
        bool addHeaderPropagation = true)
    {
        builder.ConfigureOpenTelemetry();
        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            if (addHeaderPropagation)
                http.AddHeaderPropagation();
            http.AddStandardResilienceHandler();
            http.AddServiceDiscovery();
        });

        return builder;
    }

    /// <summary>Configures open telemetry behavior for this component.</summary>
    public static IHostApplicationBuilder ConfigureOpenTelemetry(this IHostApplicationBuilder builder)
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        // The Azure Functions host process already emits request telemetry for each invocation. When this
        // worker also runs the Azure Monitor distro (which includes ASP.NET Core instrumentation), that
        // request would be reported twice with the same OperationId. Functions sets this flag so the worker
        // skips ASP.NET Core request instrumentation while still exporting its own traces, metrics, and logs.
        var suppressAspNetCoreInstrumentation = string.Equals(
            builder.Configuration["TASKFLOW_SUPPRESS_ASPNETCORE_INSTRUMENTATION"],
            "true",
            StringComparison.OrdinalIgnoreCase);

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();

                // TaskFlow's own instruments, named once here rather than per host: a meter added to a
                // shared library is then exported by every host that uses it, instead of only the host
                // whose Program.cs happened to be updated.
                metrics.AddMeter(
                    SchedulerJobMeter.MeterName,
                    CacheMeter.MeterName,
                    RateLimitingMeter.MeterName,
                    StreamingMeter.MeterName,
                    MessagingMetrics.MeterName);

                if (!suppressAspNetCoreInstrumentation)
                {
                    metrics.AddAspNetCoreInstrumentation();
                }
            })
            .WithTracing(tracing =>
            {
                tracing.AddHttpClientInstrumentation();

                // D-053: TaskFlow's own sources, named once here for the same reason the meters are - a
                // source added inside a shared library is only exported by hosts that remembered its name.
                tracing.AddSource(
                    TaskFlowActivitySources.MessagingName,
                    TaskFlowActivitySources.SchedulerName);

                if (!suppressAspNetCoreInstrumentation)
                {
                    tracing.AddAspNetCoreInstrumentation();
                }
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    /// <summary>Registers open telemetry exporters dependencies in the service container.</summary>
    private static IHostApplicationBuilder AddOpenTelemetryExporters(this IHostApplicationBuilder builder)
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(
            builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        // Enable the Azure Monitor (Application Insights) distro only when a connection string is
        // configured. This keeps local runs working without any real Azure resource while lighting
        // up traces, metrics, and logs export the moment APPLICATIONINSIGHTS_CONNECTION_STRING is set.
        var useAzureMonitor = !string.IsNullOrWhiteSpace(
            builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]);

        if (useAzureMonitor)
        {
            builder.Services.AddOpenTelemetry().UseAzureMonitor();
        }

        return builder;
    }

    /// <summary>Registers default health checks dependencies in the service container.</summary>
    public static IHostApplicationBuilder AddDefaultHealthChecks(this IHostApplicationBuilder builder)
    {
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    /// <summary>
    /// Maps the D-049 probe contract, identical on every host:
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
        app.MapHealthChecks("/healthz", new HealthCheckOptions
        {
            Predicate = _ => true
        })
        .AllowAnonymous();

        app.MapHealthChecks("/healthz/live", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live")
        })
        .AllowAnonymous();

        app.MapHealthChecks("/healthz/ready", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("ready")
        })
        .AllowAnonymous();

        return app;
    }
}
