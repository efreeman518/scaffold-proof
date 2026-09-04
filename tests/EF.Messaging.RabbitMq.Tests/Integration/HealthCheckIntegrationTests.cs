using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Testcontainers.RabbitMq;

namespace EF.Messaging.RabbitMq.Tests.Integration;

/// <summary>
/// The health check reports Healthy while the connection is open and Unhealthy once the broker is gone. The second
/// case uses a broker of its own so stopping it cannot disturb the shared fixture.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class HealthCheckIntegrationTests
{
    public TestContext TestContext { get; set; } = null!;

    private static ServiceProvider BuildWithHealthCheck(RabbitMqContainer broker, string clientName) =>
        TestServices.Build(broker, clientName, extra: null,
            configure: services => services.AddHealthChecks().AddRabbitMqHealthCheck());

    [TestMethod]
    public async Task Given_AReachableBroker_When_Checked_Then_Healthy()
    {
        CancellationToken ct = TestContext.CancellationToken;
        RabbitMqContainer broker = await RabbitMqFixture.EnsureStartedAsync(ct);

        await using ServiceProvider provider = BuildWithHealthCheck(broker, $"health-{Guid.NewGuid():N}");
        HealthReport report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(ct);

        Assert.AreEqual(HealthStatus.Healthy, report.Status, report.Entries["rabbitmq"].Description);
    }

    [TestMethod]
    public async Task Given_TheBrokerStops_When_Checked_Then_Unhealthy()
    {
        CancellationToken ct = TestContext.CancellationToken;
        await using RabbitMqContainer broker = RabbitMqFixture.NewBroker().Build();
        await broker.StartAsync(ct);

        await using ServiceProvider provider = BuildWithHealthCheck(broker, $"health-{Guid.NewGuid():N}");
        HealthCheckService checks = provider.GetRequiredService<HealthCheckService>();

        Assert.AreEqual(HealthStatus.Healthy, (await checks.CheckHealthAsync(ct)).Status);

        await broker.StopAsync(ct);

        await Poll.UntilAsync(
            async token => (await checks.CheckHealthAsync(token)).Status == HealthStatus.Unhealthy,
            TimeSpan.FromSeconds(60),
            "the health check to report Unhealthy after the broker stopped",
            ct);
    }
}
