using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client.Exceptions;
using Testcontainers.RabbitMq;

namespace EF.Messaging.RabbitMq.Tests.Integration;

/// <summary>
/// Contract 3: declaring the same topology twice is a no-op, and redeclaring a queue with different arguments
/// surfaces the broker's <c>PRECONDITION_FAILED</c> instead of being swallowed.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class TopologyIntegrationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Given_ATopology_When_DeclaredTwice_Then_TheSecondDeclarationIsANoOp()
    {
        CancellationToken ct = TestContext.CancellationToken;
        RabbitMqContainer broker = await RabbitMqFixture.EnsureStartedAsync(ct);
        string suffix = Guid.NewGuid().ToString("N");

        RabbitMqTopology topology = new(
            [new RabbitMqExchange($"x-{suffix}")],
            [new RabbitMqQueue($"q-{suffix}", DeadLetterExchange: $"x-{suffix}")],
            [new RabbitMqBinding($"q-{suffix}", $"x-{suffix}", "route.#")]);

        await using ServiceProvider provider = TestServices.Build(broker, $"topology-{suffix}");
        IRabbitMqTopologyDeclarer declarer = provider.GetRequiredService<IRabbitMqTopologyDeclarer>();

        await declarer.DeclareAsync(topology, ct);
        await declarer.DeclareAsync(topology, ct);

        using RabbitMqManagement management = RabbitMqFixture.Management(broker);
        Assert.IsGreaterThanOrEqualTo(0, await management.MessageCountAsync($"q-{suffix}", ct),
            "The queue must exist after two identical declarations.");
    }

    [TestMethod]
    public async Task Given_AnExistingQueue_When_RedeclaredWithDifferentArguments_Then_TheBrokerRejectsIt()
    {
        CancellationToken ct = TestContext.CancellationToken;
        RabbitMqContainer broker = await RabbitMqFixture.EnsureStartedAsync(ct);
        string queue = $"mismatch-{Guid.NewGuid():N}";

        await using ServiceProvider provider = TestServices.Build(broker, $"topology-{Guid.NewGuid():N}");
        IRabbitMqTopologyDeclarer declarer = provider.GetRequiredService<IRabbitMqTopologyDeclarer>();

        await declarer.DeclareAsync(
            new RabbitMqTopology([], [new RabbitMqQueue(queue,
                Arguments: new Dictionary<string, object?> { ["x-message-ttl"] = 60_000 })], []),
            ct);

        OperationInterruptedException exception = await Assert.ThrowsExactlyAsync<OperationInterruptedException>(
            () => declarer.DeclareAsync(
                new RabbitMqTopology([], [new RabbitMqQueue(queue,
                    Arguments: new Dictionary<string, object?> { ["x-message-ttl"] = 30_000 })], []),
                ct));

        Assert.Contains("PRECONDITION_FAILED", exception.Message, StringComparison.Ordinal);
    }
}
