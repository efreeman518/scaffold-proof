using EF.Common.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TaskFlow.Infrastructure.Messaging.RabbitMq;

namespace Test.Unit.Infrastructure;

/// <summary>
/// The consumer host declares its topology through the package startup, first among the hosted services so the
/// queues exist before any consumer subscribes, and without the distributed lock the TaskFlow startup used to
/// wait on: identical declarations are idempotent, and a changed definition fails whether or not two replicas
/// overlap, so the lock only added up to a minute of startup wait.
/// Pure-unit tier: service registration only, no broker.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class RabbitMqTopologyRegistrationTests
{
    [TestMethod]
    public async Task ConsumerRegistration_DeclaresTopologyFirst_ThroughThePackageStartup_WithoutALock()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{RabbitMqRegistration.OptionsSection}:ConnectionString"] = "amqp://guest:guest@localhost:5672/"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTaskFlowRabbitMqMessaging(config);
        services.AddTaskFlowRabbitMqConsumers(config);

        await using var provider = services.BuildServiceProvider();
        var first = provider.GetServices<IHostedService>().First();

        // The package startup is internal to EF.Messaging.RabbitMq, so it is identified by name.
        Assert.AreEqual("EF.Messaging.RabbitMq.RabbitMqTopologyStartup", first.GetType().FullName,
            "topology must be declared by the package startup before any consumer hosted service starts");
        Assert.IsNull(provider.GetService<IDistributedLock>(), "topology declaration no longer takes a distributed lock");
    }
}
