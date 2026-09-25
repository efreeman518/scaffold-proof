using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenTelemetry.Trace;

namespace Test.Unit.Hosting;

/// <summary>
/// D-063..D-065 ServiceDefaults contracts: unsafe methods are never retried, shutdown drains readiness inside
/// the shutdown budget, and the head-sampling ratio is honored and range-checked.
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
        var handler = new FailingHandler();
        using var client = BuildDefaultsClient(handler);

        using var response = await client.PostAsync(new Uri("http://localhost/tasks"), content: null, TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual(1, handler.Attempts);
    }

    /// <summary>A GET keeps the standard retries: one attempt plus three retries.</summary>
    [TestMethod]
    public async Task StandardHandler_TransientFailureOnGet_IsRetried()
    {
        var handler = new FailingHandler();
        using var client = BuildDefaultsClient(handler);

        using var response = await client.GetAsync(new Uri("http://localhost/tasks"), TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual(4, handler.Attempts);
    }

    /// <summary>Stopping flips readiness to unhealthy and holds for the drain delay before returning.</summary>
    [TestMethod]
    public async Task HostLifecycle_Stopping_ReportsUnhealthyAndWaitsForDrain()
    {
        var lifecycle = new HostLifecycleService(TimeSpan.FromMilliseconds(150), NullLogger<HostLifecycleService>.Instance);
        var context = new HealthCheckContext();

        Assert.AreEqual(HealthStatus.Healthy, (await lifecycle.CheckHealthAsync(context, TestContext.CancellationToken)).Status);

        var clock = Stopwatch.StartNew();
        await lifecycle.StoppingAsync(TestContext.CancellationToken);

        Assert.IsTrue(clock.Elapsed >= TimeSpan.FromMilliseconds(140), $"drain returned after {clock.Elapsed}");
        Assert.IsTrue(lifecycle.IsDraining);
        Assert.AreEqual(HealthStatus.Unhealthy, (await lifecycle.CheckHealthAsync(context, TestContext.CancellationToken)).Status);
    }

    /// <summary>The shutdown budget binds from configuration and the drain check is tagged ready.</summary>
    [TestMethod]
    public void HostLifecycle_Registration_BindsShutdownBudgetAndReadinessCheck()
    {
        var builder = CreateBuilder(new() { ["Hosting:ShutdownTimeoutSeconds"] = "12", ["Hosting:DrainDelaySeconds"] = "3" });
        builder.AddHostLifecycle();
        using var provider = builder.Services.BuildServiceProvider();

        Assert.AreEqual(TimeSpan.FromSeconds(12), provider.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout);
        var registration = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .Single(r => r.Name == HostLifecycleService.HealthCheckName);
        CollectionAssert.Contains(registration.Tags.ToList(), "ready");
    }

    /// <summary>A drain that does not fit inside the shutdown budget fails startup.</summary>
    [TestMethod]
    public void HostLifecycle_DrainNotBelowShutdownBudget_FailsStartup()
    {
        var builder = CreateBuilder(new() { ["Hosting:ShutdownTimeoutSeconds"] = "10", ["Hosting:DrainDelaySeconds"] = "10" });

        Assert.ThrowsExactly<InvalidOperationException>(() => builder.AddHostLifecycle());
    }

    /// <summary>Ratio 0 drops a root span and ratio 1 records it.</summary>
    [TestMethod]
    [DataRow("0", false)]
    [DataRow("1", true)]
    public void Tracing_SampleRatio_AppliesToRootSpans(string ratio, bool expectRecorded)
    {
        // A source only this provider listens to, so another test's TracerProvider cannot sample it in.
        var sourceName = $"Test.Sampling.{Guid.NewGuid():N}";
        var builder = CreateBuilder(new() { ["OpenTelemetry:Tracing:SampleRatio"] = ratio });
        builder.ConfigureOpenTelemetry();
        builder.Services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddSource(sourceName));
        using var provider = builder.Services.BuildServiceProvider();
        _ = provider.GetRequiredService<TracerProvider>();

        using var source = new ActivitySource(sourceName);
        using var activity = source.StartActivity("probe");

        Assert.AreEqual(expectRecorded, activity?.Recorded ?? false);
    }

    /// <summary>An out-of-range ratio fails startup instead of silently exporting everything or nothing.</summary>
    [TestMethod]
    public void Tracing_SampleRatioOutOfRange_FailsStartup()
    {
        var builder = CreateBuilder(new() { ["OpenTelemetry:Tracing:SampleRatio"] = "1.5" });

        Assert.ThrowsExactly<InvalidOperationException>(() => builder.ConfigureOpenTelemetry());
    }

    private HttpClient BuildDefaultsClient(HttpMessageHandler handler)
    {
        var builder = CreateBuilder([]);
        builder.AddServiceDefaults(addHeaderPropagation: false);
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

    /// <summary>Always answers 503, the canonical transient status, and counts attempts.</summary>
    private sealed class FailingHandler : HttpMessageHandler
    {
        private int _attempts;

        internal int Attempts => Volatile.Read(ref _attempts);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
}
