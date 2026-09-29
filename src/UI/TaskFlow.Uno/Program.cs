#if __WASM__
using System.Runtime.InteropServices.JavaScript;
using EF.UI.Client.Configuration;
using Uno.UI.Hosting;

namespace TaskFlow.Uno;

/// <summary>Bootstraps the Uno application host for the selected platform target.</summary>
public class Program
{
    static async Task Main(string[] args)
    {
        // The /app-config.json runtime base-URL contract, validated (absolute http/https, no user info, query or fragment).
        Uri runtimeGatewayUrl;
        using (var http = new HttpClient { BaseAddress = new Uri(WebAssemblyRuntimeConfig.GetCurrentOrigin()) })
        {
            runtimeGatewayUrl = await RuntimeClientConfiguration.LoadBaseUrlAsync(http);
        }

        var host = UnoPlatformHostBuilder.Create()
            .App(() => new App(runtimeGatewayUrl.AbsoluteUri))
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
