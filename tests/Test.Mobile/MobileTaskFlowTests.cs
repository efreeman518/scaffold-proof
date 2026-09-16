namespace Test.Mobile;

/// <summary>
/// Stable mobile UI accessibility checks for native Uno surfaces. Avoids deep
/// scrolling because UiAutomator2 is unreliable against long Skia forms.
/// </summary>
[TestClass]
[TestCategory("MobileUI")]
public sealed class MobileTaskFlowTests : MobileUiTestBase
{
    [TestMethod]
    [Timeout(180_000, CooperativeCancellation = true)]
    public void TaskEditor_TopLevelControls_AreAccessible_ThroughNativeUi() => RunMobileFlow(() =>
    {
        App.Shell.StartNewTask();
        App.TaskEditor.WaitUntilReady();

        Assert.IsTrue(App.TaskEditor.HasText("Task title"), "Title field accessibility label missing.");
        Assert.IsTrue(App.TaskEditor.HasText("Task description"), "Description field accessibility label missing.");
        Assert.IsTrue(App.TaskEditor.HasText("Task priority"), "Priority field accessibility label missing.");
        Assert.IsTrue(App.TaskEditor.HasText("Task status"), "Status field accessibility label missing.");
        App.TaskEditor.RequireAccessibleSave();
    });
}
