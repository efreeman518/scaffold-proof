using EF.Testing.Architecture;

namespace Test.Architecture;

/// <summary>
/// Bans synchronous waits on asynchronous work in <c>src/</c> (D-055). One <c>.Result</c> on a request path
/// blocks a thread-pool thread for the duration of the I/O, and under load that is how a host deadlocks or
/// collapses into thread-pool starvation - a failure that looks like a slow database, not like a bad line of
/// code. Nothing else in the suite would attribute it, and code review misses one call in 900 files.
/// Pure-unit tier (Roslyn syntax sweep over repository sources via EF.Testing.Architecture): no DI, host, or network.
/// </summary>
[TestClass]
[TestCategory("Architecture")]
public class NoBlockingCallsTests
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
        var result = SourceRules.NoBlockingWaits(RepoFiles.Root, RepoFiles.SourceFiles, AllowList);

        Assert.IsTrue(result.IsSuccessful,
            "Blocking waits on asynchronous work found in src/. Await the call instead, or add the file to "
            + "the allow-list with a reason if the call site genuinely cannot be asynchronous: " + result);
    }

    /// <summary>
    /// Verifies the sweep is not vacuous: the one allow-listed file must still be detected. Without this a
    /// broken detector - or a moved source tree - would report the whole repository clean forever.
    /// </summary>
    [TestMethod]
    public void Given_TheAllowListedFile_When_Swept_Then_IsStillDetected()
    {
        Assert.IsTrue(RepoFiles.SourceFiles.Count > 100,
            $"Only {RepoFiles.SourceFiles.Count} source files found under {RepoFiles.Root}; the sweep is "
            + "pointed at the wrong directory.");

        var stale = SourceRules.StaleAllowListEntries(
            SourceRules.NoBlockingWaits(RepoFiles.Root, RepoFiles.SourceFiles), AllowList);

        Assert.IsEmpty(stale,
            "The detector must still flag every allow-listed file. A file that stopped being flagged should be "
            + "removed from the allow-list: " + string.Join(", ", stale));
    }
}
