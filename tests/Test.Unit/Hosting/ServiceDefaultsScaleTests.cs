using EF.Testing.Http;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;

namespace Test.Unit.Hosting;

/// <summary>
/// D-063 ServiceDefaults contract: unsafe methods are never retried. The D-064 drain and D-065 sampling ratio
/// are EF.Host / EF.OpenTelemetry behavior and are tested in those packages.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class ServiceDefaultsScaleTests
{
    /// <summary>MSTest-injected context; supplies the per-test cancellation token.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>A POST that fails transiently is sent exactly once: a retried create is a duplicated write.</summary>
    [TestMethod]
    public async Task StandardHandler_TransientFailureOnPost_IsNotRetried()
    {
        var handler = StubHttpMessageHandler.Returns(HttpStatusCode.ServiceUnavailable);
        using var client = BuildDefaultsClient(handler);

        using var response = await client.PostAsync(new Uri("http://localhost/tasks"), content: null, TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual(1, handler.Attempts);
    }

    /// <summary>A GET keeps the standard retries: one attempt plus three retries.</summary>
    [TestMethod]
    public async Task StandardHandler_TransientFailureOnGet_IsRetried()
    {
        var handler = StubHttpMessageHandler.Returns(HttpStatusCode.ServiceUnavailable);
        using var client = BuildDefaultsClient(handler);

        using var response = await client.GetAsync(new Uri("http://localhost/tasks"), TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual(4, handler.Attempts);
    }

    private HttpClient BuildDefaultsClient(HttpMessageHandler handler)
    {
        var builder = CreateBuilder([]);
        builder.AddServiceDefaults();
        // Keep the standard retry count but not its multi-second backoff.
        builder.Services.PostConfigureAll<HttpStandardResilienceOptions>(o => o.Retry.Delay = TimeSpan.FromMilliseconds(1));
        builder.Services.AddHttpClient("defaults").ConfigurePrimaryHttpMessageHandler(() => handler);

        var provider = builder.Services.BuildServiceProvider();
        TestContext.CancellationToken.Register(provider.Dispose);
        return provider.GetRequiredService<IHttpClientFactory>().CreateClient("defaults");
    }

    private static HostApplicationBuilder CreateBuilder(Dictionary<string, string?> settings)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(settings);
        return builder;
    }
}
