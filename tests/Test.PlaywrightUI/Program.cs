using Microsoft.Playwright;
using Test.PlaywrightUI.Hosting;

namespace Test.PlaywrightUI;

internal static class Program
{
    public static async Task<int> Main()
    {
        try
        {
            await using var host = await PlaywrightAspireHost.StartAsync(["blazor"], CancellationToken.None);
            await host.RunWithinStartupBudgetAsync(
                "Gateway/Blazor browser launch and smoke",
                token => GatewayBlazorSmokeRunner.RunAsync(host.GatewayBaseUrl, host.BlazorBaseUrl, token),
                CancellationToken.None);
            Console.WriteLine("Gateway/Blazor Playwright smoke passed.");
            return 0;
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Playwright browser executable is missing.");
            Console.Error.WriteLine("Run: pwsh -NoProfile -File tests\\Test.PlaywrightUI\\bin\\Debug\\net10.0\\playwright.ps1 install chromium");
            Console.Error.WriteLine(ex.Message);
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Gateway/Blazor Playwright smoke failed: {ex.Message}");
            return 1;
        }
    }
}
