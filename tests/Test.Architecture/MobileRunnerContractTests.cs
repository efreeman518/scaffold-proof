namespace Test.Architecture;

[TestClass]
[TestCategory("Architecture")]
public sealed class MobileRunnerContractTests
{
    [TestMethod]
    public void Given_CurrentAndroidBuild_When_MobileRunnerStarts_Then_InstallsExactApk()
    {
        var runner = File.ReadAllText(RepoFiles.Path("tests", "Test.Mobile", "run-mobile-tests.ps1"));

        StringAssert.Contains(runner, "& $adb install -r $apkPath");
        StringAssert.Contains(runner, "if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }");
    }
}
