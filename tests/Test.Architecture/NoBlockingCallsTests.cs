using System.Text.RegularExpressions;

namespace Test.Architecture;

/// <summary>
/// Bans synchronous waits on asynchronous work in <c>src/</c> (D-055). One <c>.Result</c> on a request path
/// blocks a thread-pool thread for the duration of the I/O, and under load that is how a host deadlocks or
/// collapses into thread-pool starvation - a failure that looks like a slow database, not like a bad line of
/// code. Nothing else in the suite would attribute it, and code review misses one call in 900 files.
/// Pure-unit tier (regex sweep over repository sources): no DI, host, or network.
/// </summary>
[TestClass]
[TestCategory("Architecture")]
public partial class NoBlockingCallsTests
{
    /// <summary>
    /// Files permitted to block, by repo-relative path. Each entry needs a reason:
    /// <list type="bullet">
    /// <item>MockHttpMessageHandler - a test double implementing the synchronous
    /// <c>HttpMessageHandler.Send</c> override, where there is no asynchronous caller to await into.</item>
    /// </list>
    /// </summary>
    private static readonly string[] AllowList =
    [
        "src/UI/TaskFlow.Uno.Core/Business/Services/MockHttpMessageHandler.cs"
    ];

    /// <summary>Verifies no source file outside the allow-list blocks on a Task.</summary>
    [TestMethod]
    public void Given_SourceTree_When_Swept_Then_NoBlockingWaitsOutsideAllowList()
    {
        var violations = Sweep().Where(v => !IsAllowed(v.File)).ToList();

        Assert.AreEqual(0, violations.Count,
            "Blocking waits on asynchronous work found in src/. Await the call instead, or add the file to "
            + "the allow-list with a reason if the call site genuinely cannot be asynchronous:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations.Select(v => $"  {v.File}:{v.Line}: {v.Text}")));
    }

    /// <summary>
    /// Verifies the sweep is not vacuous: the one allow-listed file must still be detected. Without this a
    /// broken regex - or a moved source tree - would report the whole repository clean forever.
    /// </summary>
    [TestMethod]
    public void Given_TheAllowListedFile_When_Swept_Then_IsStillDetected()
    {
        Assert.IsTrue(RepoFiles.SourceFiles.Count > 100,
            $"Only {RepoFiles.SourceFiles.Count} source files found under {RepoFiles.Root}; the sweep is "
            + "pointed at the wrong directory.");

        var detected = Sweep().Select(v => v.File).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        CollectionAssert.AreEquivalent(AllowList, detected,
            "The detector must still flag every allow-listed file and nothing else. A file that stopped "
            + "being flagged should be removed from the allow-list: " + string.Join(", ", detected));
    }

    private static bool IsAllowed(string repoRelativePath) =>
        AllowList.Contains(repoRelativePath, StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<(string File, int Line, string Text)> Sweep()
    {
        foreach (var path in RepoFiles.SourceFiles)
        {
            var relative = Path.GetRelativePath(RepoFiles.Root, path).Replace('\\', '/');
            var lines = File.ReadAllLines(path);

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                // Comments and doc comments talk about these APIs on purpose (this file included).
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("///", StringComparison.Ordinal)
                    || trimmed.StartsWith('*'))
                    continue;

                if (BlockingCall().IsMatch(line))
                    yield return (relative, i + 1, trimmed);
            }
        }
    }

    /// <summary>
    /// Approximates "synchronously wait on a Task-like" from source text. Task.Result has no syntactic
    /// marker distinguishing it from any other Result property, so the pattern targets the three shapes
    /// that appear in practice - a member access on a call result, a statement-terminating access, and an
    /// access inside an argument list - plus Wait() and GetAwaiter().GetResult(). Over-matching is fine:
    /// a false positive is one allow-list entry with a reason, which is a review the code should have had.
    /// </summary>
    [GeneratedRegex(@"\)\.Result\b|\.Result;|\.Result\)|\.Wait\(|\.GetAwaiter\(\)\.GetResult\(\)",
        RegexOptions.CultureInvariant)]
    private static partial Regex BlockingCall();
}
