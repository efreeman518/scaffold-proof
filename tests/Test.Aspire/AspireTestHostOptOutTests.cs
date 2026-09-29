using EF.Testing.Environment;

namespace Test.Aspire;

/// <summary>
/// Fast check that the one opt-out parser shared by the Aspire host and its surface tests accepts every
/// documented spelling, so a skipped surface is always reported as an opt-out rather than a failure.
/// </summary>
[TestClass]
[TestCategory("Aspire")]
[DoNotParallelize]
public sealed class AspireTestHostOptOutTests
{
    private const string VariableName = "TASKFLOW_TEST_OPT_OUT_PROBE";

    [TestMethod]
    [DataRow("false", true)]
    [DataRow("0", true)]
    [DataRow("no", true)]
    [DataRow("NO", true)]
    [DataRow("true", false)]
    [DataRow("1", false)]
    [DataRow(null, false)]
    public void Given_AnOptOutValue_When_Checked_Then_MatchesTheDocumentedSpellings(string? value, bool expected)
    {
        using var environment = new EnvironmentVariableScope().Set(VariableName, value);

        Assert.AreEqual(expected, AspireTestHost.IsExplicitlyDisabled(VariableName));
    }

    /// <summary>
    /// Test prerequisite rule: a missing optional prerequisite is Inconclusive on a default run (lane switch unset)
    /// and a failure once the operator explicitly enabled that lane.
    /// </summary>
    [TestMethod]
    [DataRow(null, false)]
    [DataRow("true", true)]
    [DataRow("1", true)]
    [DataRow("yes", true)]
    public void Given_AMissingPrerequisite_When_Reported_Then_FailsOnlyWhenTheLaneIsExplicitlyEnabled(
        string? value,
        bool expectFailure)
    {
        using var environment = new EnvironmentVariableScope().Set(VariableName, value);

        void Report() => AspireTestHost.ReportMissingPrerequisite(VariableName, "Run `enable-probe`.");

        var outcome = expectFailure
            ? (Exception)Assert.ThrowsExactly<AssertFailedException>(Report)
            : Assert.ThrowsExactly<AssertInconclusiveException>(Report);
        StringAssert.Contains(outcome.Message, "Run `enable-probe`.");
    }
}
