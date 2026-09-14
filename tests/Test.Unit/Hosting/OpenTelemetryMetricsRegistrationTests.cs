using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
