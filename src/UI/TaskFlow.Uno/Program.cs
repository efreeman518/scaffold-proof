#if __WASM__
using System.Runtime.InteropServices.JavaScript;
using TaskFlow.Uno.Core.Client;
using Uno.UI.Hosting;

namespace TaskFlow.Uno;

/// <summary>Bootstraps the Uno application host for the selected platform target.</summary>
public class Program
{
    static async Task Main(string[] args)
    {
        var runtimeGatewayUrl = await RuntimeGatewayConfiguration.LoadAsync(
            new HttpClient { BaseAddress = new Uri(WebAssemblyRuntimeConfig.GetCurrentOrigin()) });
        var host = UnoPlatformHostBuilder.Create()
            .App(() => new App(runtimeGatewayUrl))
            .UseWebAssembly()
            .Build();

        await host.RunAsync();
    }
}

internal static partial class WebAssemblyRuntimeConfig
{
    [JSImport("globalThis.taskFlowRuntimeConfig.currentOrigin")]
    internal static partial string GetCurrentOrigin();
}
#endif
