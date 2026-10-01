using EF.Testing.Environment;
using EF.Testing.Processes;
using Microsoft.Playwright;
using Test.PlaywrightUI.Hosting;

namespace Test.PlaywrightUI;

/// <summary>
/// MSTest adapter for existing TypeScript Playwright browser projects.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TypeScriptPlaywrightSuiteTests
{
    private const string PlaywrightLaneVariable = "TASKFLOW_PLAYWRIGHT_TESTS_ENABLED";
    private const string WasmLaneVariable = "TASKFLOW_WASM_TESTS_ENABLED";

    /// <summary>
    /// Gets MSTest context command output.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Runs the Blazor TypeScript project and C# Gateway/Blazor smoke through one Aspire-owned host.
    /// </summary>
    [TestMethod]
    [TestCategory("PlaywrightUI")]
    [Timeout(3_600_000, CooperativeCancellation = true)]
    public Task BlazorTypeScriptProject_Passes() =>
        RunTypeScriptProjectsAsync(["blazor"], runGatewayBlazorSmoke: true);

    /// <summary>
    /// Runs the React TypeScript Playwright project when that host is available.
    /// </summary>
    [TestMethod]
    [TestCategory("PlaywrightUI")]
    [Timeout(3_600_000, CooperativeCancellation = true)]
    public Task ReactTypeScriptProject_Passes() => RunTypeScriptProjectsAsync(["react"]);

    /// <summary>
    /// Runs the Uno WASM TypeScript Playwright canvas smoke project.
    /// </summary>
    [TestMethod]
    [TestCategory("WasmUI")]
    [Timeout(3_600_000, CooperativeCancellation = true)]
    public Task UnoWasmCanvasSmoke_Passes() =>
        RunTypeScriptProjectsAsync(["uno-release-cold-start", "uno"]);

    private async Task RunTypeScriptProjectsAsync(
        string[] requestedProjects,
        bool runGatewayBlazorSmoke = false)
    {
        if (TestEnvironment.IsFalse(PlaywrightLaneVariable))
        {
            Assert.Inconclusive("TASKFLOW_PLAYWRIGHT_TESTS_ENABLED=false - Playwright full-stack tier opted out.");
            return;
        }

        if (requestedProjects.Any(project => project.StartsWith("uno", StringComparison.OrdinalIgnoreCase))
            && TestEnvironment.IsFalse(WasmLaneVariable))
        {
            Assert.Inconclusive("TASKFLOW_WASM_TESTS_ENABLED=false - Uno WASM full-stack tier opted out.");
            return;
        }

        var readiness = TypeScriptPlaywrightRunner.CheckReadiness();
        TestContext.WriteLine(readiness.Message);
        if (!readiness.CanRun)
        {
            ReportMissingPrerequisite(PlaywrightLaneVariable, readiness.Message);
            return;
        }

        PlaywrightAspireHost host;
        try
        {
            try
            {
                host = await PlaywrightAspireHost.StartAsync(requestedProjects, TestContext.CancellationToken);
            }
            catch (PlaywrightAspireHost.ResourceUnavailableException ex)
            {
                // DCP intermittently reports every resource FailedToStart before creating its log directory.
                // Retry only that pre-launch state once; remove when the Aspire launcher no longer reproduces it.
                TestContext.WriteLine($"Aspire pre-launch failed once; retrying with a fresh host: {ex.Message}");
                host = await PlaywrightAspireHost.StartAsync(requestedProjects, TestContext.CancellationToken);
            }
        }
        // Test prerequisite rule: no container runtime or no wasm-tools workload is Inconclusive with the enabling
        // command on a default run and a failure when the lane is explicitly enabled. Once Docker passed its
        // preflight, an AppHost or resource that does not come up (a second pre-launch failure or the startup
        // deadline included) fails the test.
        catch (PlaywrightAspireHost.DockerUnavailableException ex)
        {
            ReportMissingPrerequisite(PlaywrightLaneVariable, ex.Message);
            return;
        }
        catch (WasmPrerequisiteException ex)
        {
            ReportMissingPrerequisite(
                WasmLaneVariable,
                $"Uno WASM prerequisite missing (or set {WasmLaneVariable}=false to opt out): {ex.Message}");
            return;
        }
        await using var hostScope = host;
        foreach (var message in host.DiagnosticMessages)
        {
            TestContext.WriteLine(message);
        }

        var availableProjects = host.TypeScriptProjects.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedProjects = requestedProjects.Where(availableProjects.Contains).ToArray();
        if (selectedProjects.Length == 0)
        {
            Assert.Fail(
                $"Requested TypeScript Playwright project(s) unavailable: {string.Join(", ", requestedProjects)}. "
                + $"Available: {string.Join(", ", host.TypeScriptProjects)}.");
            return;
        }

        ProcessResult result;
        try
        {
            await host.RunWithinStartupBudgetAsync(
                "Gateway readiness smoke",
                token => GatewayHttpSmokeRunner.RunAsync(host.GatewayBaseUrl, token),
                TestContext.CancellationToken);

            if (runGatewayBlazorSmoke)
            {
                await host.RunWithinStartupBudgetAsync(
                    "Gateway/Blazor browser launch and smoke",
                    token => GatewayBlazorSmokeRunner.RunAsync(host.GatewayBaseUrl, host.BlazorBaseUrl, token),
                    TestContext.CancellationToken);
            }

            result = await host.RunWithinStartupBudgetAsync(
                "Playwright browser launch and smoke",
                token => TypeScriptPlaywrightRunner.RunAsync(selectedProjects, readiness, token),
                TestContext.CancellationToken);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Equals("Node.js is not available on PATH.", StringComparison.Ordinal))
        {
            ReportMissingPrerequisite(
                PlaywrightLaneVariable,
                "Node.js is not available on PATH. Install Node.js LTS so `node --version` succeeds, "
                + "or set TASKFLOW_PLAYWRIGHT_TESTS_ENABLED=false to opt out.");
            return;
        }
        catch (PlaywrightException ex) when (
            ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase))
        {
            ReportMissingPrerequisite(
                PlaywrightLaneVariable,
                "Playwright browser executable is unavailable. Run `npx --prefix tests/Test.PlaywrightUI playwright install chromium`, "
                + "or set TASKFLOW_PLAYWRIGHT_TESTS_ENABLED=false to opt out. " + ex.Message);
            return;
        }
        TestContext.WriteLine(result.StandardOutput);
        TestContext.WriteLine(result.StandardError);

        if (!result.Succeeded)
        {
            await host.DumpDiagnosticsAsync(CancellationToken.None);
            Assert.Fail(
                (result.TimedOut
                    ? "TypeScript Playwright timed out"
                    : $"TypeScript Playwright failed with exit code {result.ExitCode}")
                + $" project(s): {string.Join(", ", selectedProjects)}."
                + Environment.NewLine
                + "stdout:"
                + Environment.NewLine
                + Truncate(result.StandardOutput)
                + Environment.NewLine
                + "stderr:"
                + Environment.NewLine
                + Truncate(result.StandardError));
        }
    }

    private static string Truncate(string value)
        => value.Length <= 12_000 ? value : value[..12_000] + "...";

    /// <summary>
    /// Test prerequisite rule for a missing optional prerequisite: Inconclusive on a default run, with the enabling
    /// command in <paramref name="message"/>; a failure when the operator explicitly enabled the lane.
    /// </summary>
    private static void ReportMissingPrerequisite(string laneVariable, string message)
    {
        if (TestEnvironment.IsTrue(laneVariable))
        {
            Assert.Fail($"{laneVariable} is explicitly enabled, but a prerequisite is missing. {message}");
        }

        Assert.Inconclusive(message);
    }
}
