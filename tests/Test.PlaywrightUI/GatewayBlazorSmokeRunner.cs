using EF.Testing.Http;
using Microsoft.Playwright;
using Test.PlaywrightUI.PageObjects;

namespace Test.PlaywrightUI;

/// <summary>
/// Runs the stable Gateway and Blazor happy-path browser smoke.
/// </summary>
internal static class GatewayBlazorSmokeRunner
{
    /// <summary>
    /// Runs the smoke-test workflow against already-started Gateway and Blazor endpoints.
    /// </summary>
    public static async Task RunAsync(string gatewayBaseUrl, string blazorBaseUrl, CancellationToken cancellationToken)
    {
        // Probe HTTP readiness before browser startup so failures point to hosting when the app is down.
        await HttpReadiness.WaitAsync(new Uri($"{gatewayBaseUrl.TrimEnd('/')}/healthz/ready"), cancellationToken: cancellationToken);
        await HttpReadiness.WaitAsync(new Uri($"{blazorBaseUrl.TrimEnd('/')}/healthz/ready"), cancellationToken: cancellationToken);

        using var playwright = await Playwright.CreateAsync().WaitAsync(cancellationToken);
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Timeout = 120_000
        }).WaitAsync(cancellationToken);

        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true
        }).WaitAsync(cancellationToken);
        var page = await context.NewPageAsync().WaitAsync(cancellationToken);

        var gateway = new GatewayPageObject(page);
        await gateway.AssertRootRespondsAsync(gatewayBaseUrl);
        await gateway.AssertAliveRespondsAsync(gatewayBaseUrl);

        var tasks = new BlazorTaskListPageObject(page);
        await tasks.NavigateAndAssertReadyAsync(blazorBaseUrl);
    }
}
