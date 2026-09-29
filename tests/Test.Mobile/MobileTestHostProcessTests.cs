using EF.Testing.Processes;
using System.Diagnostics;

namespace Test.Mobile;

/// <summary>
/// Verifies a cancelled package build does not leave its child process running: the host runs its dotnet
/// restore/build through <see cref="ProcessRunner"/>, which kills the process tree before cancellation propagates.
/// Needs no device or Appium: a long-running shell command that records its PID stands in for the build child.
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
        var pidFile = Path.Combine(Path.GetTempPath(), $"taskflow-mobile-{Guid.NewGuid():N}.pid");
        var options = OperatingSystem.IsWindows()
            ? new ProcessRunOptions
            {
                FileName = "powershell.exe",
                Arguments = ["-NoProfile", "-Command", $"Set-Content -LiteralPath '{pidFile}' -Value $PID; Start-Sleep -Seconds 120"],
                Timeout = TimeSpan.FromMinutes(5)
            }
            : new ProcessRunOptions
            {
                FileName = "sh",
                Arguments = ["-c", $"echo $$ > '{pidFile}'; exec sleep 120"],
                Timeout = TimeSpan.FromMinutes(5)
            };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        try
        {
            var run = ProcessRunner.RunAsync(options, cancellation.Token);
            while (!run.IsCompleted && !(File.Exists(pidFile) && new FileInfo(pidFile).Length > 0))
                await Task.Delay(50, TestContext.CancellationToken);
            Assert.IsFalse(run.IsCompleted, "The stand-in child must still be running when the wait is cancelled.");

            await cancellation.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => run);

            var pid = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
            Assert.ThrowsExactly<ArgumentException>(
                () => Process.GetProcessById(pid),
                "The child must be killed, not orphaned, when the wait is cancelled.");
        }
        finally
        {
            File.Delete(pidFile);
        }
    }
}
