namespace Test.Mobile;

/// <summary>
/// Makes the Android mobile lane runnable from Test Explorer without a separate package build or Appium command.
/// </summary>
[TestClass]
public static class MobileTestAssemblyHooks
{
    [AssemblyInitialize]
    public static async Task AssemblyInitialize(TestContext context)
    {
        var settings = MobileTestSettings.From(context);
        if (settings.Enabled)
        {
            await MobileTestHost.EnsureReadyAsync(settings, context.CancellationToken);
        }
    }

    [AssemblyCleanup]
    public static Task AssemblyCleanup(TestContext _) => MobileTestHost.StopAsync();
}
