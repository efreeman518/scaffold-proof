using System.Text.RegularExpressions;

namespace Test.Architecture;

/// <summary>
/// Bans the ambient clock in <c>src/</c>. Every time reading goes through an injected <see cref="TimeProvider"/>
/// (the hosts register <c>TimeProvider.System</c>; the DbContexts carry it as <c>Clock</c>), and the domain takes
/// the reading as a method argument, so a test pins time with a fake provider instead of racing the wall
/// clock, and a time-dependent rule (a completed date, an overdue filter, a recurrence seed) can be asserted
/// exactly. One <c>DateTime.UtcNow</c> in a domain method is enough to make that rule untestable again.
/// Pure-unit tier: file reads under the repo, no DI, host, or network.
/// </summary>
[TestClass]
[TestCategory("Architecture")]
public partial class NoAmbientClockTests
{
    /// <summary>Matches a read of the ambient clock, not the word appearing in an identifier such as <c>GeneratedAtUtc</c>.</summary>
    [GeneratedRegex(@"\b(DateTime|DateTimeOffset)\s*\.\s*(UtcNow|Now|Today)\b")]
    private static partial Regex AmbientClockRead();

    /// <summary>Verifies no source file reads <c>DateTime.UtcNow</c>, <c>DateTimeOffset.UtcNow</c>, <c>.Now</c> or <c>.Today</c>.</summary>
    [TestMethod]
    public void Given_SourceTree_When_Swept_Then_NoAmbientClockReads()
    {
        var offenders = new List<string>();
        foreach (var file in RepoFiles.SourceFiles)
        {
            var lines = File.ReadAllLines(file);
            for (var lineNumber = 0; lineNumber < lines.Length; lineNumber++)
            {
                // A per-line strip of anything after "//" skips comments; this rule does not need a C# parser.
                var commentStart = lines[lineNumber].IndexOf("//", StringComparison.Ordinal);
                var codeOnly = commentStart >= 0 ? lines[lineNumber][..commentStart] : lines[lineNumber];
                if (AmbientClockRead().IsMatch(codeOnly))
                    offenders.Add($"{Path.GetRelativePath(RepoFiles.Root, file)}:{lineNumber + 1}");
            }
        }

        Assert.IsEmpty(offenders,
            "Ambient clock reads in src/. Inject TimeProvider (or use the DbContext Clock) and pass the reading "
            + "into domain methods: " + string.Join(", ", offenders));
    }

    /// <summary>Verifies the detector is not vacuous: it flags each banned form and ignores look-alike identifiers.</summary>
    [TestMethod]
    public void Given_SampleLines_When_Matched_Then_FlagsOnlyClockReads()
    {
        Assert.IsTrue(RepoFiles.SourceFiles.Count > 100,
            $"Only {RepoFiles.SourceFiles.Count} source files found under {RepoFiles.Root}; the sweep is "
            + "pointed at the wrong directory.");

        string[] flagged = ["var now = DateTime.UtcNow;", "x = DateTimeOffset.UtcNow.AddDays(1);", "DateTime.Now", "DateTime.Today"];
        string[] ignored = ["GeneratedAtUtc = timeProvider.GetUtcNow()", "var nowUtc = clock.GetUtcNow();", "DateTimeOffset now"];

        foreach (var line in flagged)
            Assert.IsTrue(AmbientClockRead().IsMatch(line), $"The detector missed: {line}");
        foreach (var line in ignored)
            Assert.IsFalse(AmbientClockRead().IsMatch(line), $"The detector flagged a non-clock line: {line}");
    }
}
