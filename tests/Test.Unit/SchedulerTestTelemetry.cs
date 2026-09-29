using System.Diagnostics.Metrics;
using EF.BackgroundServices.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Test.Unit;

/// <summary>The EF.BackgroundServices job telemetry the scheduler handlers record through, over a real meter factory.</summary>
internal static class SchedulerTestTelemetry
{
    private static readonly IMeterFactory MeterFactory =
        new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();

    public static ScheduledJobTelemetry Create() => new(MeterFactory, Options.Create(new ScheduledJobTelemetryOptions()));
}
