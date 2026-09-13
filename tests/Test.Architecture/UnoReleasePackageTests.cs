namespace Test.Architecture;

[TestClass]
[TestCategory("Architecture")]
public sealed class UnoReleasePackageTests
{
    [TestMethod]
    public void Given_UnoReleaseBuild_When_ProjectFileRead_Then_KeepsOptimizationForSdkAssetExclusion()
    {
        var project = File.ReadAllText(RepoFiles.Path("src", "UI", "TaskFlow.Uno", "TaskFlow.Uno.csproj"));

        StringAssert.Contains(project, "<Optimize Condition=\"'$(Configuration)'=='Release'\">true</Optimize>");
        Assert.IsFalse(project.Contains("_RemoveUnoDevServerFromRelease", StringComparison.Ordinal));
    }
}
