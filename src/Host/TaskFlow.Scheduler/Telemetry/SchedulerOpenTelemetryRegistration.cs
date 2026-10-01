using EF.BackgroundServices.Scheduling;
using EF.Messaging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace TaskFlow.Scheduler.Telemetry;

/// <summary>Registers Scheduler-specific OpenTelemetry instruments.</summary>
public static class SchedulerOpenTelemetryRegistration
{
    /// <summary>
    /// Exports the EF.BackgroundServices job telemetry (<see cref="ScheduledJobTelemetryOptions"/> names: the
    /// <c>scheduler.job.*</c> and <c>scheduler.retention.rows_deleted</c> instruments, one span per job run) and the
    /// messaging meters; metrics only when metric collection is enabled.
    /// </summary>
    public static IServiceCollection AddSchedulerOpenTelemetry(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var names = new ScheduledJobTelemetryOptions();
        var telemetry = services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddSource(names.ActivitySourceName));

        if (configuration.GetValue("OpenTelemetry:MetricsEnabled", true))
        {
            telemetry.WithMetrics(metrics => metrics.AddMeter(
                names.MeterName,
                MessagingMetrics.MeterName,
                "EF.Messaging.RabbitMq"));
        }

        return services;
    }
}
