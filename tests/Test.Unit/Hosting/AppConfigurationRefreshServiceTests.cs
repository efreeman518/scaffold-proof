using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using TaskFlow.Scheduler.Infrastructure;

namespace Test.Unit.Hosting;

/// <summary>
/// The Scheduler has no HTTP pipeline to trigger Azure App Configuration refresh, so without this loop a sentinel
/// change or feature-flag flip never reached it until restart (D-042).
/// Pure-unit tier: mocked refreshers, no App Configuration store.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class AppConfigurationRefreshServiceTests
{
    /// <summary>MSTest-injected context; supplies the per-test cancellation token.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>With App Configuration configured, every refresher is asked to refresh on each tick.</summary>
    [TestMethod]
    public async Task Given_AppConfiguration_When_Running_Then_RefreshersAreCalledEachTick()
    {
        var calls = 0;
        var reachedThree = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresher = Refresher(() =>
        {
            if (Interlocked.Increment(ref calls) == 3) reachedThree.TrySetResult();
            return true;
        });
        using var service = Create(refresher, out _);

        await service.StartAsync(TestContext.CancellationToken);
        await reachedThree.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);
        await service.StopAsync(TestContext.CancellationToken);

        Assert.IsGreaterThanOrEqualTo(3, Volatile.Read(ref calls));
        Assert.IsTrue(service.ExecuteTask!.IsCompletedSuccessfully, "stopping must end the loop without a fault");
    }

    /// <summary>Without App Configuration there is no refresher provider and the service does nothing.</summary>
    [TestMethod]
    public async Task Given_NoAppConfiguration_When_Started_Then_TheLoopExitsImmediately()
    {
        using var service = new AppConfigurationRefreshService(
            new ServiceCollection().BuildServiceProvider(),
            new RecordingLogger(),
            TimeProvider.System)
        {
            Interval = TimeSpan.FromMilliseconds(5)
        };

        await service.StartAsync(TestContext.CancellationToken);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);

        Assert.IsTrue(service.ExecuteTask.IsCompletedSuccessfully);
    }

    /// <summary>A failure streak warns once, not every tick, and recovery is reported once.</summary>
    [TestMethod]
    public async Task Given_FailingRefreshes_When_Running_Then_WarnsOncePerStreakAndReportsRecovery()
    {
        var calls = 0;
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresher = Refresher(() =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 5) recovered.TrySetResult();
            return call > 3;
        });
        using var service = Create(refresher, out var logger);

        await service.StartAsync(TestContext.CancellationToken);
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);
        await service.StopAsync(TestContext.CancellationToken);

        Assert.AreEqual(1, logger.Count(LogLevel.Warning), "three failed refreshes in a row are one streak");
        Assert.AreEqual(1, logger.Count(LogLevel.Information), "the first success after the streak is reported once");
    }

    private static IConfigurationRefresher Refresher(Func<bool> result)
    {
        var refresher = new Mock<IConfigurationRefresher>();
        refresher.Setup(r => r.TryRefreshAsync(It.IsAny<CancellationToken>())).ReturnsAsync(result);
        return refresher.Object;
    }

    private static AppConfigurationRefreshService Create(IConfigurationRefresher refresher, out RecordingLogger logger)
    {
        var provider = new Mock<IConfigurationRefresherProvider>();
        provider.Setup(p => p.Refreshers).Returns([refresher]);
        var services = new ServiceCollection().AddSingleton(provider.Object).BuildServiceProvider();
        logger = new RecordingLogger();
        return new AppConfigurationRefreshService(services, logger, TimeProvider.System)
        {
            Interval = TimeSpan.FromMilliseconds(5)
        };
    }

    private sealed class RecordingLogger : ILogger<AppConfigurationRefreshService>
    {
        private readonly List<LogLevel> _levels = [];

        public int Count(LogLevel level)
        {
            lock (_levels) return _levels.Count(l => l == level);
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_levels) _levels.Add(logLevel);
        }
    }
}
