using Microsoft.Playwright;
using Test.PlaywrightUI.Hosting;
using Test.Support.Aspire;

namespace Test.PlaywrightUI;

/// <summary>
/// MSTest adapter for existing TypeScript Playwright browser projects.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TypeScriptPlaywrightSuiteTests
{
    /// <summary>
    /// Gets MSTest context command output.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Runs the Blazor TypeScript Playwright project through the Aspire-owned host.
    /// </summary>
    [TestMethod]
    [TestCategory("PlaywrightUI")]
    [Timeout(3_600_000, CooperativeCancellation = true)]
    public Task BlazorTypeScriptProject_Passes() => RunTypeScriptProjectsAsync("blazor");

    /// <summary>
    /// Runs the React TypeScript Playwright project when that host is available.
    /// </summary>
    [TestMethod]
    [TestCategory("PlaywrightUI")]
    [Timeout(3_600_000, CooperativeCancellation = true)]
    public Task ReactTypeScriptProject_Passes() => RunTypeScriptProjectsAsync("react");

    /// <summary>
    /// Runs the Uno WASM TypeScript Playwright canvas smoke project.
    /// </summary>
    [TestMethod]
    [TestCategory("WasmUI")]
    [Timeout(3_600_000, CooperativeCancellation = true)]
    public Task UnoWasmCanvasSmoke_Passes() =>
        RunTypeScriptProjectsAsync("uno-release-cold-start", "uno");

    /// <summary>Runs the C# Gateway and Blazor browser happy-path through the same Aspire host policy.</summary>
    [TestMethod]
    [TestCategory("PlaywrightUI")]
    [Timeout(3_600_000, CooperativeCancellation = true)]
    public async Task GatewayBlazorBrowserSmoke_Passes()
    {
        if (IsExplicitlyDisabled("TASKFLOW_PLAYWRIGHT_TESTS_ENABLED"))
        {
            Assert.Inconclusive("TASKFLOW_PLAYWRIGHT_TESTS_ENABLED=false - Playwright full-stack tier opted out.");
            return;
        }

        PlaywrightAspireHost host;
        try
        {
            host = await PlaywrightAspireHost.StartAsync(["blazor"], TestContext.CancellationToken);
        }
        catch (PlaywrightAspireHost.DockerUnavailableException ex)
        {
            Assert.Inconclusive(ex.Message);
            return;
        }
        catch (TimeoutException ex)
        {
            Assert.Inconclusive($"Aspire resources unavailable: {ex.Message}");
            return;
        }

        await using var hostScope = host;
        try
        {
            await host.RunWithinStartupBudgetAsync(
                "Gateway/Blazor browser launch and smoke",
                token => GatewayBlazorSmokeRunner.RunAsync(host.GatewayBaseUrl, host.BlazorBaseUrl, token),
                TestContext.CancellationToken);
        }
        catch (PlaywrightException ex) when (
            ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive("Playwright browser executable is unavailable: " + ex.Message);
        }
    }

    private async Task RunTypeScriptProjectsAsync(params string[] requestedProjects)
    {
        if (IsExplicitlyDisabled("TASKFLOW_PLAYWRIGHT_TESTS_ENABLED"))
        {
            Assert.Inconclusive("TASKFLOW_PLAYWRIGHT_TESTS_ENABLED=false - Playwright full-stack tier opted out.");
            return;
        }

        if (requestedProjects.Any(project => project.StartsWith("uno", StringComparison.OrdinalIgnoreCase))
            && IsExplicitlyDisabled("TASKFLOW_WASM_TESTS_ENABLED"))
        {
            Assert.Inconclusive("TASKFLOW_WASM_TESTS_ENABLED=false - Uno WASM full-stack tier opted out.");
            return;
        }

        var readiness = TypeScriptPlaywrightRunner.CheckReadiness();
        TestContext.WriteLine(readiness.Message);
        if (!readiness.CanRun)
        {
            Assert.Inconclusive(readiness.Message);
            return;
        }

        PlaywrightAspireHost host;
        try
        {
            host = await PlaywrightAspireHost.StartAsync(requestedProjects, TestContext.CancellationToken);
        }
        catch (PlaywrightAspireHost.DockerUnavailableException ex)
        {
            Assert.Inconclusive(ex.Message);
            return;
        }
        catch (TimeoutException ex)
        {
            Assert.Inconclusive($"Aspire resources unavailable: {ex.Message}");
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

        CommandResult result;
        try
        {
            await host.RunWithinStartupBudgetAsync(
                "Gateway readiness smoke",
                token => GatewayHttpSmokeRunner.RunAsync(host.GatewayBaseUrl, token),
                TestContext.CancellationToken);

            result = await host.RunWithinStartupBudgetAsync(
                "Playwright browser launch and smoke",
                token => TypeScriptPlaywrightRunner.RunAsync(selectedProjects, token),
                TestContext.CancellationToken);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Equals("Node.js is not available on PATH.", StringComparison.Ordinal))
        {
            Assert.Inconclusive(ex.Message);
            return;
        }
        TestContext.WriteLine(result.StandardOutput);
        TestContext.WriteLine(result.StandardError);

        if (result.ExitCode != 0)
        {
            await host.DumpDiagnosticsAsync(CancellationToken.None);
            Assert.Fail(
                $"TypeScript Playwright failed with exit code {result.ExitCode} project(s): {string.Join(", ", selectedProjects)}."
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

    private static bool IsExplicitlyDisabled(string variableName)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        return string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase);
    }
}
