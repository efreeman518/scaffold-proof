using System.Diagnostics;

namespace Test.Mobile;

/// <summary>
/// Verifies a cancelled package build does not leave its child process running. Needs no device or Appium:
/// a long-running shell command stands in for the dotnet restore/build child.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class MobileTestHostProcessTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Given_ARunningChild_When_WaitIsCancelled_Then_ChildIsKilledBeforeCancellationPropagates()
    {
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("powershell.exe", "-NoProfile -Command Start-Sleep -Seconds 120")
            : new ProcessStartInfo("sleep", "120");
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the stand-in child process.");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => MobileTestHost.WaitForExitOrKillAsync(process, cancellation.Token));

        Assert.IsTrue(process.HasExited, "The child must be killed, not orphaned, when the wait is cancelled.");
    }
}
