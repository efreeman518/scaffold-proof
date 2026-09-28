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

    [TestMethod]
    public void DockerPreflight_EveryUnavailableReasonNamesTheEnablingStep()
    {
        var source = File.ReadAllText(RepoRoot.Combine("tests", "Test.Support", "Hosting", "DockerRuntimePreflight.cs"));

        var reasons = Regex.Matches(source, "\"Container runtime unavailable:[^;]*;", RegexOptions.Singleline);

        Assert.IsNotEmpty(reasons);
        foreach (Match reason in reasons)
            StringAssert.Contains(reason.Value, "{EnablingStep}");
    }

    private static bool IsBuildOutput(string file)
    {
        var separator = Path.DirectorySeparatorChar;
        return file.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase)
            || file.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase)
            || file.Contains($"{separator}node_modules{separator}", StringComparison.OrdinalIgnoreCase);
    }
}
