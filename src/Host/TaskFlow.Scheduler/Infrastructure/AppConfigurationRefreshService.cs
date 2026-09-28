using Microsoft.Extensions.Configuration.AzureAppConfiguration;

namespace TaskFlow.Scheduler.Infrastructure;

/// <summary>
/// Drives Azure App Configuration refresh on the Scheduler (D-042). The Api and Gateway refresh from their
/// request middleware and Functions from its worker middleware; the Scheduler serves no application traffic,
/// so without this the sentinel key and feature-flag flips (the AiTaskReviewer flags it evaluates, among
/// others) would not reach it until a restart. Ticks at the provider's own 30-second refresh interval;
/// <see cref="IConfigurationRefresher.TryRefreshAsync"/> reports its own failures and keeps the last good values.
/// </summary>
public sealed class AppConfigurationRefreshService(IConfigurationRefresherProvider refresherProvider) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            foreach (var refresher in refresherProvider.Refreshers)
            {
                await refresher.TryRefreshAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
