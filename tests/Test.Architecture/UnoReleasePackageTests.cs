namespace Test.Architecture;

[TestClass]
[TestCategory("Architecture")]
public sealed class UnoReleasePackageTests
{
    [TestMethod]
    public void Given_UnoDockerfile_When_Read_Then_NativeWasmCompilerHasPython()
    {
        var dockerfile = File.ReadAllText(RepoFiles.Path("src", "UI", "TaskFlow.Uno", "Dockerfile"));

        StringAssert.Contains(dockerfile, "apt-get install -y --no-install-recommends python3 python-is-python3");
        StringAssert.Contains(dockerfile, "rm -rf /var/lib/apt/lists/*");
    }

    [TestMethod]
    public void Given_UnoReleaseBuild_When_ProjectFileRead_Then_KeepsOptimizationForSdkAssetExclusion()
    {
        var project = File.ReadAllText(RepoFiles.Path("src", "UI", "TaskFlow.Uno", "TaskFlow.Uno.csproj"));

        StringAssert.Contains(project, "<Optimize Condition=\"'$(Configuration)'=='Release'\">true</Optimize>");
        Assert.IsFalse(project.Contains("_RemoveUnoDevServerFromRelease", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Given_TaskItemRoute_When_PageReceivesRawModel_Then_WrapsGeneratedMvuxProxy()
    {
        var page = File.ReadAllText(RepoFiles.Path("src", "UI", "TaskFlow.Uno", "Views", "TaskItemPage.xaml.cs"));
        var xaml = File.ReadAllText(RepoFiles.Path("src", "UI", "TaskFlow.Uno", "Views", "TaskItemPage.xaml"));
        var viewModel = File.ReadAllText(RepoFiles.Path(
            "src", "UI", "TaskFlow.Uno", "Views", "TaskItemPageBindableViewModel.cs"));
        var mobileDriver = File.ReadAllText(RepoFiles.Path(
            "tests", "Test.Mobile", "PageObjects", "MobileTaskAppDriver.cs"));

        StringAssert.Contains(page, "DataContext is TaskItemPageModel model");
        StringAssert.Contains(page, "new TaskItemPageBindableViewModel(model)");
        StringAssert.Contains(page, "TaskFormScrollViewer.ChangeView(null, 0");
        StringAssert.Contains(viewModel, "TaskItemPageViewModel(model)");
        StringAssert.Contains(xaml, "AutomationProperties.HelpText=\"{Binding Title}\"");
        StringAssert.Contains(mobileDriver, "@hint = {literal}");
    }
}
