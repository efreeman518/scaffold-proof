using EF.IntegrationTesting.Environment;

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
}
