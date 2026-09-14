using OpenTelemetry.Metrics;
using TaskFlow.Observability.Meters;

namespace TaskFlow.Scheduler.Telemetry;

/// <summary>Registers Scheduler-specific OpenTelemetry instruments.</summary>
public static class SchedulerOpenTelemetryRegistration
{
    /// <summary>Adds Scheduler meters when metric collection is enabled.</summary>
    public static IServiceCollection AddSchedulerOpenTelemetry(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        if (configuration.GetValue("OpenTelemetry:MetricsEnabled", true))
        {
            services.AddOpenTelemetry()
                .WithMetrics(metrics => metrics.AddMeter(
                    SchedulingMetrics.MeterName,
                    MessagingMetrics.MeterName,
                    "EF.Messaging.RabbitMq"));
        }

        return services;
    }
}
