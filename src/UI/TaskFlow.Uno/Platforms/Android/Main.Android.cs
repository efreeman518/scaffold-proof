using Android.Runtime;
using Uno.UI.Hosting;
using Uno.UI.Runtime.Android;

namespace TaskFlow.Uno.Droid;

/// <summary>Provides platform entry behavior for the Uno application target.</summary>
[global::Android.App.ApplicationAttribute(
    Label = "@string/ApplicationName",
    LargeHeap = true,
    HardwareAccelerated = true,
    Theme = "@style/AppTheme"
)]
public class Application : NativeApplication
{
    /// <summary>Initializes application with required dependencies and default state.</summary>
    public Application(IntPtr javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    /// <summary>Builds the Android host for the shared app, as the iOS entry point does for UIKit.</summary>
    protected override UnoPlatformHost CreateHost() =>
        UnoPlatformHostBuilder.Create()
            .App(() => new App())
            .UseAndroid()
            .Build();
}
