using Android.App;
using Android.Views;

namespace TaskFlow.Uno.Droid;

/// <summary>Provides platform entry behavior for the Uno main target.</summary>
[Activity(
    MainLauncher = true,
    ConfigurationChanges = global::Uno.UI.ActivityHelper.AllConfigChanges,
    WindowSoftInputMode = SoftInput.AdjustNothing | SoftInput.StateHidden
)]
public class MainActivity : global::Uno.UI.Runtime.Android.ApplicationActivity
{
}
