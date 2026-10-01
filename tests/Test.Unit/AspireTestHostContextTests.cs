using EF.IntegrationTesting.Aspire;

namespace Test.Unit;

/// <summary>
/// Verifies the startup-budget contract Test.Aspire and Test.PlaywrightUI rely on from the package
/// <see cref="AspireTestHostContext"/>, without starting Docker or an AppHost.
/// Unit tier: the cumulative deadline policy is pure process orchestration logic.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class AspireTestHostContextTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Given_OneGlobalBudget_When_AStartupStepExceedsIt_Then_TimeoutNamesTheStep()
    {
        var host = new AspireTestHostContext(TimeSpan.FromMilliseconds(25));

        var exception = await Assert.ThrowsExactlyAsync<TimeoutException>(() =>
            host.RunStartupStepAsync(
                "browser launch",
                token => Task.Delay(TimeSpan.FromSeconds(5), token),
                TestContext.CancellationToken));

        StringAssert.Contains(exception.Message, "Global Aspire startup budget");
        StringAssert.Contains(exception.Message, "browser launch");
    }

    [TestMethod]
    public async Task Given_A_SubordinateStepTimeout_When_GlobalBudgetRemains_Then_OriginalFailurePropagates()
    {
        var host = new AspireTestHostContext(TimeSpan.FromSeconds(10));

        var exception = await Assert.ThrowsExactlyAsync<TimeoutException>(() =>
            host.RunStartupStepAsync(
                "Functions HTTP readiness",
                _ => Task.FromException(new TimeoutException("Functions returned HTTP 500.")),
                TestContext.CancellationToken));

        Assert.AreEqual("Functions returned HTTP 500.", exception.Message);
    }

    [TestMethod]
    public async Task Given_TwoStartupSteps_When_TheirCombinedTimeExceedsBudget_Then_SecondStepUsesOnlyRemainingTime()
    {
        var host = new AspireTestHostContext(TimeSpan.FromMilliseconds(500));

        await host.RunStartupStepAsync(
            "build host",
            token => Task.Delay(TimeSpan.FromMilliseconds(300), token),
            TestContext.CancellationToken);

        var exception = await Assert.ThrowsExactlyAsync<TimeoutException>(() =>
            host.RunStartupStepAsync(
                "browser launch",
                token => Task.Delay(TimeSpan.FromMilliseconds(300), token),
                TestContext.CancellationToken));

        StringAssert.Contains(exception.Message, "browser launch");
        StringAssert.Contains(exception.Message, "Global Aspire startup budget");
    }
}
