using System.Runtime;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// D-064 host lifecycle. On start it logs the GC configuration the process actually runs with, so the D-047
/// runtime profile is observed rather than assumed (a container limited to one CPU runs Workstation GC whatever
/// the project says). On shutdown it reports readiness unhealthy for a drain delay before any hosted service -
/// the web server included - stops, so the orchestrator takes the replica out of rotation while in-flight
/// requests still complete. <c>StoppingAsync</c> runs before every <c>StopAsync</c>, which is what makes the
/// ordering hold without depending on registration order.
/// </summary>
public sealed partial class HostLifecycleService(TimeSpan drainDelay, ILogger<HostLifecycleService> logger)
    : IHostedLifecycleService, IHealthCheck
{
    /// <summary>Health check name, tagged <c>ready</c>.</summary>
    public const string HealthCheckName = "draining";

    private int _draining;

    /// <summary>True once shutdown has begun; readiness is unhealthy from then on.</summary>
    public bool IsDraining => Volatile.Read(ref _draining) == 1;

    /// <inheritdoc />
    public Task StartedAsync(CancellationToken cancellationToken)
    {
        var gcVariables = GC.GetConfigurationVariables();
        LogRuntimeProfile(
            GCSettings.IsServerGC,
            gcVariables.GetValueOrDefault("GCDynamicAdaptationMode")?.ToString(),
            gcVariables.GetValueOrDefault("GCHeapCount")?.ToString(),
            gcVariables.GetValueOrDefault("GCHeapHardLimit")?.ToString(),
            Environment.ProcessorCount);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _draining, 1);
        if (drainDelay <= TimeSpan.Zero) return;

        LogDraining(drainDelay.TotalSeconds);
        await Task.Delay(drainDelay, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.None);
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(IsDraining
            ? HealthCheckResult.Unhealthy("Host is shutting down.")
            : HealthCheckResult.Healthy());

    /// <inheritdoc />
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Runtime profile: ServerGC={IsServerGc}, DATAS={AdaptationMode}, HeapCount={HeapCount}, HeapHardLimit={HeapHardLimit}, ProcessorCount={ProcessorCount}")]
    private partial void LogRuntimeProfile(
        bool isServerGc, string? adaptationMode, string? heapCount, string? heapHardLimit, int processorCount);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Shutdown started; readiness reports unhealthy for {DrainSeconds}s before hosted services stop")]
    private partial void LogDraining(double drainSeconds);
}
