using Microsoft.Extensions.Configuration.AzureAppConfiguration;

namespace TaskFlow.Scheduler.Infrastructure;

/// <summary>
/// Drives Azure App Configuration refresh on the Scheduler (D-042). The provider only refreshes when something
/// calls <see cref="IConfigurationRefresher.TryRefreshAsync"/>; web hosts do that per request through the
/// <c>UseAzureAppConfiguration</c> middleware, but the Scheduler serves no application requests, so without this
/// loop a sentinel change or a feature-flag flip (the AiReview flags it evaluates) would never reach it until
/// restart. When App Configuration is not configured no refresher provider is registered and the loop exits at
/// once. The provider's own refresh interval (30 s) still gates the network calls; ticking at the same interval
/// just keeps the check from lagging it.
/// </summary>
public sealed class AppConfigurationRefreshService(
    IServiceProvider services,
    ILogger<AppConfigurationRefreshService> logger,
    TimeProvider timeProvider) : BackgroundService
{
    /// <summary>How often the refreshers are asked to refresh; matches the D-042 provider refresh interval.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

    /// <summary>Tick interval; tests shorten it.</summary>
    internal TimeSpan Interval { get; init; } = DefaultInterval;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var refreshers = services.GetService<IConfigurationRefresherProvider>()?.Refreshers.ToList() ?? [];
        if (refreshers.Count == 0) return;

        var failedRefreshes = 0;
        using var timer = new PeriodicTimer(Interval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                var succeeded = true;
                foreach (var refresher in refreshers)
                {
                    // TryRefreshAsync reports failure as false (and logs the cause through the provider's own
                    // logger) instead of throwing, so the last good configuration stays in force meanwhile.
                    succeeded &= await refresher.TryRefreshAsync(stoppingToken).ConfigureAwait(false);
                }

                if (!succeeded)
                {
                    // Once per failure streak, not every tick: an App Configuration outage would otherwise log
                    // a warning every 30 seconds for its whole duration.
                    if (failedRefreshes++ == 0) logger.AppConfigurationRefreshFailed();
                }
                else if (failedRefreshes > 0)
                {
                    logger.AppConfigurationRefreshRecovered(failedRefreshes);
                    failedRefreshes = 0;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown ends the loop; nothing to report.
        }
    }
}
