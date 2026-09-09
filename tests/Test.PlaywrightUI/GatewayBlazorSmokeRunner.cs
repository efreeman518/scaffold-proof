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
        await EndpointProbe.EnsureReachableAsync($"{gatewayBaseUrl.TrimEnd('/')}/healthz/ready", "Gateway", cancellationToken);
        await EndpointProbe.EnsureReachableAsync($"{blazorBaseUrl.TrimEnd('/')}/healthz/ready", "Blazor", cancellationToken);

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

/// <summary>
/// Probes HTTP endpoints before browser startup so failures point to hosting when the app is down.
/// </summary>
internal static class EndpointProbe
{
    internal static async Task EnsureReachableAsync(string url, string name, CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await http.GetAsync(url, cancellationToken);
                if ((int)response.StatusCode < 500)
                {
                    return;
                }
            }
            catch (Exception ex) when (
                ex is HttpRequestException
                || ex is TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // Aspire can bind the endpoint before the child app finishes booting.
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        throw new InvalidOperationException($"{name} endpoint not reachable at {url}.");
    }
}
