using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using TaskFlow.Infrastructure.Caching;
using TaskFlow.Scheduler.Telemetry;

namespace Test.Unit.Hosting;

/// <summary>Guards D-061's low-volume metrics switch at every registration site.</summary>
[TestClass]
[TestCategory("Unit")]
public sealed class OpenTelemetryMetricsRegistrationTests
{
    [TestMethod]
    public void ServiceDefaults_DisabledMetrics_KeepLogsAndTracesWithoutMeterProvider()
    {
        var builder = CreateBuilder(metricsEnabled: false);
        builder.ConfigureOpenTelemetry();

        using var provider = builder.Services.BuildServiceProvider();

        Assert.IsNull(provider.GetService<MeterProvider>());
        Assert.IsNotNull(provider.GetService<TracerProvider>());
        Assert.IsTrue(provider.GetServices<ILoggerProvider>()
            .Any(loggerProvider => loggerProvider is OpenTelemetryLoggerProvider));
    }

    /// <summary>
    /// Functions sets OpenTelemetry__SuppressAspNetCoreInstrumentation because its host already reports each
    /// invocation. The Azure Monitor distro always adds ASP.NET Core instrumentation, so it must not run
    /// there; all three signals still export through the per-signal exporters.
    /// </summary>
    [TestMethod]
    public void ServiceDefaults_SuppressedAspNetCoreInstrumentation_SkipsTheDistroAndKeepsEverySignal()
    {
        var suppressed = CreateBuilder(metricsEnabled: true);
        suppressed.Configuration["OpenTelemetry:SuppressAspNetCoreInstrumentation"] = "true";
        suppressed.ConfigureOpenTelemetry();
        var distro = CreateBuilder(metricsEnabled: true);
        distro.ConfigureOpenTelemetry();

        Assert.IsFalse(RegistersAzureMonitorDistro(suppressed.Services), "the distro re-adds ASP.NET Core instrumentation");
        Assert.IsTrue(RegistersAzureMonitorDistro(distro.Services), "control: the probe detects the distro");

        using var provider = suppressed.Services.BuildServiceProvider();
        Assert.IsNotNull(provider.GetService<MeterProvider>());
        Assert.IsNotNull(provider.GetService<TracerProvider>());
        Assert.IsTrue(provider.GetServices<ILoggerProvider>()
            .Any(loggerProvider => loggerProvider is OpenTelemetryLoggerProvider));
    }

    /// <summary>
    /// The RabbitMQ transport meter (publish, confirm, consume, dead-letter) is exported. Probed with the
    /// package's own constant, so a rename in the package fails here instead of silently dropping the meter.
    /// </summary>
    [TestMethod]
    public void ServiceDefaults_ExportsTheRabbitMqTransportMeter()
    {
        var builder = CreateBuilder(metricsEnabled: true);
        builder.ConfigureOpenTelemetry();
        using var provider = builder.Services.BuildServiceProvider();
        _ = provider.GetRequiredService<MeterProvider>();

        using var meter = new System.Diagnostics.Metrics.Meter(EF.Messaging.RabbitMq.RabbitMqMetrics.MeterName);
        var probe = meter.CreateCounter<long>("taskflow.test.rabbitmq.probe");

        Assert.IsTrue(probe.Enabled, "no MeterProvider listens to the RabbitMQ transport meter");
    }

    private static bool RegistersAzureMonitorDistro(IServiceCollection services) =>
        services.Any(descriptor => descriptor.ServiceType == typeof(IConfigureOptions<AzureMonitorOptions>));

    [TestMethod]
    public void Caching_MetricsSetting_DefaultsEnabledAndCanDisableMeterProvider()
    {
        using var enabled = BuildCachingProvider(metricsEnabled: null);
        using var disabled = BuildCachingProvider(metricsEnabled: false);

        Assert.IsNotNull(enabled.GetService<MeterProvider>());
        Assert.IsNull(disabled.GetService<MeterProvider>());
    }

    [TestMethod]
    public void Scheduler_MetricsSetting_DefaultsEnabledAndCanDisableMeterProvider()
    {
        using var enabled = BuildSchedulerProvider(metricsEnabled: null);
        using var disabled = BuildSchedulerProvider(metricsEnabled: false);

        Assert.IsNotNull(enabled.GetService<MeterProvider>());
        Assert.IsNull(disabled.GetService<MeterProvider>());
    }

    private static ServiceProvider BuildCachingProvider(bool? metricsEnabled)
    {
        var configuration = CreateConfiguration(metricsEnabled);
        var services = new ServiceCollection();
        services.AddTaskFlowCaching(configuration);
        return services.BuildServiceProvider();
    }

    private static ServiceProvider BuildSchedulerProvider(bool? metricsEnabled)
    {
        var configuration = CreateConfiguration(metricsEnabled);
        var services = new ServiceCollection();
        services.AddSchedulerOpenTelemetry(configuration);
        return services.BuildServiceProvider();
    }

    private static HostApplicationBuilder CreateBuilder(bool metricsEnabled)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenTelemetry:MetricsEnabled"] = metricsEnabled.ToString(),
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://localhost:4317",
            ["APPLICATIONINSIGHTS_CONNECTION_STRING"] =
                "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://localhost/"
        });
        return builder;
    }

    private static IConfiguration CreateConfiguration(bool? metricsEnabled) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(metricsEnabled.HasValue
                ? new Dictionary<string, string?>
                {
                    ["OpenTelemetry:MetricsEnabled"] = metricsEnabled.Value.ToString()
                }
                : [])
            .Build();
}
