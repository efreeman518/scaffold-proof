using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text;
using Testcontainers.RabbitMq;

namespace EF.Messaging.RabbitMq.Tests.Integration;

/// <summary>
/// Contract 4 against a real broker: prefetch bounds concurrency, Reject dead-letters immediately, Retry is
/// bounded by MaxDeliveryCount before dead-lettering, a handler exception counts as Retry, every delivery gets a
/// fresh scope, and a graceful shutdown neither loses nor duplicates work.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class ConsumerIntegrationTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    public TestContext TestContext { get; set; } = null!;

    private sealed record Fixture(IHost Host, HandlerScript Script, string Queue, string DeadLetterQueue);

    /// <summary>Starts a host consuming a fresh queue, optionally with a dead-letter exchange behind it.</summary>
    private async Task<Fixture> StartConsumerAsync(
        RabbitMqContainer broker,
        HandlerScript script,
        ushort prefetchCount,
        int maxDeliveryCount,
        bool withDeadLetter,
        CancellationToken ct)
    {
        string suffix = Guid.NewGuid().ToString("N");
        string queue = $"work-{suffix}";
        string deadLetterExchange = $"dlx-{suffix}";
        string deadLetterQueue = $"dead-{suffix}";

        RabbitMqTopology topology = withDeadLetter
            ? new RabbitMqTopology(
                [new RabbitMqExchange(deadLetterExchange, Type: "fanout")],
                [
                    new RabbitMqQueue(queue, DeadLetterExchange: deadLetterExchange),
                    new RabbitMqQueue(deadLetterQueue)
                ],
                [new RabbitMqBinding(deadLetterQueue, deadLetterExchange, string.Empty)])
            : new RabbitMqTopology([], [new RabbitMqQueue(queue)], []);

        IHost host = await TestServices.StartHostAsync(
            broker,
            $"consumer-{suffix}",
            [
                new($"Messaging:RabbitMq:Consumers:{queue}:PrefetchCount", prefetchCount.ToString()),
                new($"Messaging:RabbitMq:Consumers:{queue}:MaxDeliveryCount", maxDeliveryCount.ToString())
            ],
            services =>
            {
                services.AddSingleton(script);
                services.AddRabbitMqTopology(topology);
                services.AddRabbitMqConsumer<ScriptedHandler>(queue);
            },
            ct);

        return new Fixture(host, script, queue, deadLetterQueue);
    }

    private static async Task PublishAsync(IHost host, string queue, int count, CancellationToken ct)
    {
        RabbitMqMessage[] messages = [.. Enumerable.Range(0, count).Select(i =>
            new RabbitMqMessage(Encoding.UTF8.GetBytes($"{{\"i\":{i}}}"), queue, $"m-{Guid.NewGuid():N}-{i}"))];

        await host.Services.GetRequiredService<IRabbitMqPublisher>().PublishBatchAsync(string.Empty, messages, ct);
    }

    [TestMethod]
    public async Task Given_PrefetchOfFour_When_HandlersBlock_Then_AtMostFourDeliveriesAreInFlight()
    {
        CancellationToken ct = TestContext.CancellationToken;
        RabbitMqContainer broker = await RabbitMqFixture.EnsureStartedAsync(ct);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        HandlerScript script = new()
        {
            Behavior = async (_, _) =>
            {
                await release.Task;
                return ConsumeResult.Ack;
            }
        };

        Fixture fixture = await StartConsumerAsync(broker, script, prefetchCount: 4, maxDeliveryCount: 5, withDeadLetter: false, ct);
        using IHost host = fixture.Host;

        await PublishAsync(host, fixture.Queue, 12, ct);

        await Poll.UntilAsync(_ => Task.FromResult(script.InFlight == 4), Bound, "four deliveries to be in flight", ct);

        using RabbitMqManagement management = RabbitMqFixture.Management(broker);
        int unacknowledged = await Poll.UntilAsync(
            token => management.UnacknowledgedAsync(fixture.Queue, token),
            count => count > 0,
            Bound,
            "the management API to report unacknowledged deliveries",
            ct);

        Assert.IsLessThanOrEqualTo(4, unacknowledged, "The broker holds more unacknowledged deliveries than the prefetch count.");
        Assert.IsLessThanOrEqualTo(4, script.PeakInFlight, "More handlers ran concurrently than the prefetch count allows.");

        release.SetResult();
        await Poll.UntilAsync(_ => Task.FromResult(script.Count == 12), Bound, "all twelve deliveries to be handled", ct);
        Assert.IsLessThanOrEqualTo(4, script.PeakInFlight);
    }

    [TestMethod]
    public async Task Given_ARejectResult_When_Handled_Then_TheMessageLandsInTheDeadLetterQueue()
    {
        CancellationToken ct = TestContext.CancellationToken;
        RabbitMqContainer broker = await RabbitMqFixture.EnsureStartedAsync(ct);

        HandlerScript script = new() { Behavior = (_, _) => Task.FromResult(ConsumeResult.Reject("unsupported")) };
        Fixture fixture = await StartConsumerAsync(broker, script, prefetchCount: 4, maxDeliveryCount: 5, withDeadLetter: true, ct);
        using IHost host = fixture.Host;

        await PublishAsync(host, fixture.Queue, 1, ct);

        using RabbitMqManagement management = RabbitMqFixture.Management(broker);
        await Poll.UntilAsync(
            token => management.MessageCountAsync(fixture.DeadLetterQueue, token),
            count => count == 1,
            Bound,
            "the rejected message to reach the dead-letter queue",
            ct);

        Assert.AreEqual(1, script.Count, "A rejected message must not be redelivered.");
    }

    [TestMethod]
    public async Task Given_RepeatedRetries_When_MaxDeliveryCountIsReached_Then_TheMessageIsDeadLettered()
    {
        CancellationToken ct = TestContext.CancellationToken;
        RabbitMqContainer broker = await RabbitMqFixture.EnsureStartedAsync(ct);

        HandlerScript script = new() { Behavior = (_, _) => Task.FromResult(ConsumeResult.Retry("transient")) };
        Fixture fixture = await StartConsumerAsync(broker, script, prefetchCount: 4, maxDeliveryCount: 3, withDeadLetter: true, ct);
        using IHost host = fixture.Host;

        await PublishAsync(host, fixture.Queue, 1, ct);

        using RabbitMqManagement management = RabbitMqFixture.Management(broker);
        await Poll.UntilAsync(
            token => management.MessageCountAsync(fixture.DeadLetterQueue, token),
            count => count == 1,
            Bound,
            "the exhausted message to reach the dead-letter queue",
            ct);

        Assert.AreEqual(3, script.Count, "The message must be delivered exactly MaxDeliveryCount times.");
        Assert.IsTrue(script.Deliveries.Skip(1).All(delivery => delivery.Redelivered), "Later attempts must be redeliveries.");
    }

    [TestMethod]
    public async Task Given_AThrowingHandler_When_Handled_Then_ItCountsAsRetryAndIsDeadLettered()
    {
        CancellationToken ct = TestContext.CancellationToken;
        RabbitMqContainer broker = await RabbitMqFixture.EnsureStartedAsync(ct);

        HandlerScript script = new() { Behavior = (_, _) => throw new InvalidOperationException("handler blew up") };
        Fixture fixture = await StartConsumerAsync(broker, script, prefetchCount: 4, maxDeliveryCount: 2, withDeadLetter: true, ct);
        using IHost host = fixture.Host;

        await PublishAsync(host, fixture.Queue, 1, ct);

        using RabbitMqManagement management = RabbitMqFixture.Management(broker);
        await Poll.UntilAsync(
            token => management.MessageCountAsync(fixture.DeadLetterQueue, token),
            count => count == 1,
            Bound,
            "the failing message to reach the dead-letter queue",
            ct);

        Assert.AreEqual(2, script.Count, "A handler exception must count as a retry, bounded by MaxDeliveryCount.");
    }

    [TestMethod]
    public async Task Given_AScopedHandler_When_ThreeDeliveriesArrive_Then_EachGetsItsOwnScope()
    {
        CancellationToken ct = TestContext.CancellationToken;
        RabbitMqContainer broker = await RabbitMqFixture.EnsureStartedAsync(ct);

        string queue = $"scopes-{Guid.NewGuid():N}";
        ScopeLog log = new();

        using IHost host = await TestServices.StartHostAsync(
            broker,
            $"consumer-{Guid.NewGuid():N}",
            [new($"Messaging:RabbitMq:Consumers:{queue}:PrefetchCount", "1")],
            services =>
            {
                services.AddSingleton(log);
                services.AddScoped<ScopeMarker>();
                services.AddRabbitMqTopology(new RabbitMqTopology([], [new RabbitMqQueue(queue)], []));
                services.AddRabbitMqConsumer<ScopeCapturingHandler>(queue);
            },
            ct);

        await PublishAsync(host, queue, 3, ct);

        await Poll.UntilAsync(_ => Task.FromResult(log.Ids.Count == 3), Bound, "three deliveries to be handled", ct);
        Assert.HasCount(3, log.Ids.Distinct(), "Deliveries shared a service scope.");
    }

    [TestMethod]
    public async Task Given_InFlightHandlers_When_TheHostStops_Then_EveryMessageIsHandledExactlyOnce()
    {
        CancellationToken ct = TestContext.CancellationToken;
        RabbitMqContainer broker = await RabbitMqFixture.EnsureStartedAsync(ct);

        HandlerScript script = new()
        {
            Behavior = async (_, token) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300), token);
                return ConsumeResult.Ack;
            }
        };

        Fixture fixture = await StartConsumerAsync(broker, script, prefetchCount: 16, maxDeliveryCount: 5, withDeadLetter: false, ct);
        using IHost host = fixture.Host;

        await PublishAsync(host, fixture.Queue, 6, ct);
        await Poll.UntilAsync(_ => Task.FromResult(script.InFlight > 0), Bound, "the first handler to start", ct);

        await host.StopAsync(ct);

        Assert.AreEqual(6, script.Count, "The drain must finish every in-flight handler.");
        Assert.HasCount(6, script.Deliveries.Select(delivery => delivery.MessageId).Distinct(),
            "A message was handled twice across the shutdown.");

        using RabbitMqManagement management = RabbitMqFixture.Management(broker);
        int remaining = await Poll.UntilAsync(
            token => management.MessageCountAsync(fixture.Queue, token),
            count => count == 0,
            Bound,
            "the queue to be empty after a graceful shutdown",
            ct);

        Assert.AreEqual(0, remaining);
    }
}
