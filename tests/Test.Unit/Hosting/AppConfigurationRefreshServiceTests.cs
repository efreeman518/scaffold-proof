using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using Moq;
using TaskFlow.Scheduler.Infrastructure;

namespace Test.Unit.Hosting;

/// <summary>
/// D-042 on the Scheduler: it serves no application traffic, so nothing but this service ever asks App
/// Configuration to refresh - without it sentinel and feature-flag changes wait for a restart.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class AppConfigurationRefreshServiceTests
{
    /// <summary>MSTest-injected context; supplies the per-test cancellation token.</summary>
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task Started_RefreshesEveryRegisteredRefresher()
    {
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresher = new Mock<IConfigurationRefresher>();
        refresher.Setup(r => r.TryRefreshAsync(It.IsAny<CancellationToken>()))
            .Callback(() => refreshed.TrySetResult())
            .ReturnsAsync(true);
        var provider = new Mock<IConfigurationRefresherProvider>();
        provider.Setup(p => p.Refreshers).Returns([refresher.Object]);
        using var service = new AppConfigurationRefreshService(provider.Object);

        await service.StartAsync(TestContext.CancellationToken);
        await refreshed.Task.WaitAsync(TestContext.CancellationToken);
        await service.StopAsync(TestContext.CancellationToken);

        refresher.Verify(r => r.TryRefreshAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce());
    }
}
