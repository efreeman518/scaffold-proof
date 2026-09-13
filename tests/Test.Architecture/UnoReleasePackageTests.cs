namespace Test.Architecture;

[TestClass]
[TestCategory("Architecture")]
public sealed class UnoReleasePackageTests
{
    [TestMethod]
    public void Given_UnoReleaseBuild_When_ProjectFileRead_Then_RemovesDevelopmentServerAfterImplicitResolution()
    {
        var project = File.ReadAllText(RepoFiles.Path("src", "UI", "TaskFlow.Uno", "TaskFlow.Uno.csproj"));

        StringAssert.Contains(project, "<Target Name=\"_RemoveUnoDevServerFromRelease\"");
        StringAssert.Contains(project, "AfterTargets=\"UnoImplicitPackages\"");
        StringAssert.Contains(project, "BeforeTargets=\"CollectPackageReferences\"");
        StringAssert.Contains(project, "Condition=\"'$(Configuration)' == 'Release'\"");
        StringAssert.Contains(project, "<PackageReference Remove=\"Uno.WinUI.DevServer\" />");
    }
}
