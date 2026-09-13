using Microsoft.Extensions.Hosting;
using TaskFlow.Uno.Views;

namespace TaskFlow.Uno;

/// <summary>Configures Uno application startup, dependency injection, and host-specific services.</summary>
public partial class App : Application
{
    public static Window? MainWindow { get; private set; }
    public static IHost? Host { get; private set; }

    /// <summary>Initializes app with required dependencies and default state.</summary>
    public App(string? runtimeGatewayUrl = null)
    {
        RuntimeGatewayUrl = runtimeGatewayUrl;
        this.InitializeComponent();
    }

    private string? RuntimeGatewayUrl { get; }

    /// <summary>Handles launched events for app.</summary>
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var builder = this.CreateBuilder(args);
        ConfigureAppBuilder(builder);
        MainWindow = builder.Window;

        Host = await builder.NavigateAsync<Shell>();
    }
}
