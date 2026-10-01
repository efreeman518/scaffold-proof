using EF.Testing.Processes;
using System.Text.RegularExpressions;

namespace Test.Unit.Infrastructure;

/// <summary>
/// Test prerequisite rule for the host-backed tiers: on a default run a missing optional prerequisite (no container
/// runtime, no Functions Core Tools, Node dependencies not restored) reports Inconclusive and names its enabling command
/// or opt-out variable, while an explicitly enabled lane fails on it (Test.Aspire AspireTestHostOptOutTests covers that
/// branch); a prerequisite that is present but fails to start, and any app or host startup failure, fails the test.
/// Locks the two ways the tiers drifted: a startup failure mapped to Inconclusive, and a Docker reason with no way to
/// enable it.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class TestPrerequisiteContractTests
{
    /// <summary>MSTest-injected context; supplies the per-test cancellation token.</summary>
    public TestContext TestContext { get; set; } = null!;

    private static readonly string[] HostBackedTiers =
        ["Test.Support", "Test.E2E", "Test.Integration", "Test.Aspire", "Test.PlaywrightUI", "Test.Mobile"];

    private static readonly Regex StartupFailureAsInconclusive = new(
        @"Assert\.Inconclusive\([^;]*StartupError"
        + @"|catch\s*\((?:TimeoutException|PlaywrightAspireHost\.ResourceUnavailableException)[^)]*\)\s*\{[^}]*Assert\.Inconclusive",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    [TestMethod]
    public void HostBackedTiers_NeverReportAStartupFailureAsInconclusive()
    {
        var files = HostBackedTiers
            .SelectMany(tier => Directory.EnumerateFiles(RepoRoot.Combine("tests", tier), "*.cs", SearchOption.AllDirectories))
            .Where(file => !IsBuildOutput(file))
            .ToList();
        Assert.IsNotEmpty(files);

        foreach (var file in files)
            Assert.IsFalse(StartupFailureAsInconclusive.IsMatch(File.ReadAllText(file)), file);
    }

    /// <summary>A missing runtime is reported (never thrown) with the enabling step, so Inconclusive says how to run it.</summary>
    [TestMethod]
    public async Task DockerPreflight_UnavailableReasonNamesTheEnablingStep()
    {
        var reason = await DockerRuntimePreflight.GetUnavailableReasonAsync(
            TimeSpan.FromSeconds(10), TestContext.CancellationToken, executable: "taskflow-no-such-container-runtime");

        Assert.IsNotNull(reason);
        StringAssert.Contains(reason, "Container runtime unavailable");
        StringAssert.Contains(reason, "Start a Docker-compatible runtime");
    }

    private static bool IsBuildOutput(string file)
    {
        var separator = Path.DirectorySeparatorChar;
        return file.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase)
            || file.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase)
            || file.Contains($"{separator}node_modules{separator}", StringComparison.OrdinalIgnoreCase);
    }
}
