using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using System.Text;
using Testcontainers.RabbitMq;

namespace EF.Messaging.RabbitMq.Tests.Integration;

/// <summary>
/// Contract 2 against a real broker: a large batch is confirmed inside the timeout, an unroutable exchange throws
/// <see cref="RabbitMqPublishException"/>, and every published property and header survives the round trip.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class PublisherIntegrationTests
{
    public TestContext TestContext { get; set; } = null!;

    private static RabbitMqTopology QueueOnly(string queue) =>
        new([], [new RabbitMqQueue(queue)], []);

    [TestMethod]
    public async Task Given_ABatchOfAThousand_When_Published_Then_AllAreConfirmedAndQueued()
    {
        CancellationToken ct = TestContext.CancellationToken;
        RabbitMqContainer broker = await RabbitMqFixture.EnsureStartedAsync(ct);
        string queue = $"batch-{Guid.NewGuid():N}";

        await using ServiceProvider provider = TestServices.Build(broker, $"publisher-{Guid.NewGuid():N}");
        await provider.GetRequiredService<IRabbitMqTopologyDeclarer>().DeclareAsync(QueueOnly(queue), ct);

        RabbitMqMessage[] messages = [.. Enumerable.Range(0, 1000).Select(i =>
            new RabbitMqMessage(Encoding.UTF8.GetBytes($"{{\"i\":{i}}}"), queue, $"m-{i}"))];

        // The default exchange routes by routing key to the queue of the same name.
        await provider.GetRequiredService<IRabbitMqPublisher>().PublishBatchAsync(string.Empty, messages, ct);

        using RabbitMqManagement management = RabbitMqFixture.Management(broker);
        int queued = await Poll.UntilAsync(
            token => management.MessageCountAsync(queue, token),
            count => count >= messages.Length,
            TimeSpan.FromSeconds(30),
            $"all {messages.Length} confirmed messages to appear on {queue}",
            ct);

        Assert.AreEqual(messages.Length, queued);
    }

    [TestMethod]
    public async Task Given_AMissingExchange_When_Published_Then_ThrowsWithUnconfirmedIndices()
    {
        CancellationToken ct = TestContext.CancellationToken;
        RabbitMqContainer broker = await RabbitMqFixture.EnsureStartedAsync(ct);

        await using ServiceProvider provider = TestServices.Build(broker, $"publisher-{Guid.NewGuid():N}");
        RabbitMqMessage[] messages =
        [
            new(Encoding.UTF8.GetBytes("{}"), "key", "m-0"),
            new(Encoding.UTF8.GetBytes("{}"), "key", "m-1")
        ];

        RabbitMqPublishException exception = await Assert.ThrowsExactlyAsync<RabbitMqPublishException>(
            () => provider.GetRequiredService<IRabbitMqPublisher>()
                .PublishBatchAsync($"missing-{Guid.NewGuid():N}", messages, ct));

        Assert.IsNotEmpty(exception.UnconfirmedIndices);
        Assert.Contains(0, exception.UnconfirmedIndices);
    }

    [TestMethod]
    public async Task Given_PropertiesAndHeaders_When_Published_Then_TheyRoundTrip()
    {
        CancellationToken ct = TestContext.CancellationToken;
        RabbitMqContainer broker = await RabbitMqFixture.EnsureStartedAsync(ct);
        string queue = $"props-{Guid.NewGuid():N}";

        await using ServiceProvider provider = TestServices.Build(broker, $"publisher-{Guid.NewGuid():N}");
        await provider.GetRequiredService<IRabbitMqTopologyDeclarer>().DeclareAsync(QueueOnly(queue), ct);

        RabbitMqMessage message = new(
            Encoding.UTF8.GetBytes("""{"ok":true}"""),
            queue,
            MessageId: "message-42",
            ContentType: "application/json",
            CorrelationId: "correlation-42",
            Headers: new Dictionary<string, object?>
            {
                ["EventType"] = "TaskCreated",
                ["Attempt"] = 3,
                ["Sequence"] = 9_000_000_000L,
                ["Replay"] = true,
                ["Raw"] = new byte[] { 7, 8 }
            });

        await provider.GetRequiredService<IRabbitMqPublisher>().PublishAsync(string.Empty, message, ct);

        IRabbitMqConnectionMultiplexer multiplexer = provider.GetRequiredService<IRabbitMqConnectionMultiplexer>();
        IConnection connection = await multiplexer.GetConnectionAsync(ct);
        await using IChannel channel = await connection.CreateChannelAsync(cancellationToken: ct);

        BasicGetResult? received = await Poll.UntilAsync(
            token => channel.BasicGetAsync(queue, autoAck: true, token),
            result => result is not null,
            TimeSpan.FromSeconds(15),
            $"the published message to arrive on {queue}",
            ct);

        Assert.IsNotNull(received);
        Assert.AreEqual("message-42", received.BasicProperties.MessageId);
        Assert.AreEqual("correlation-42", received.BasicProperties.CorrelationId);
        Assert.AreEqual("application/json", received.BasicProperties.ContentType);
        Assert.IsTrue(received.BasicProperties.Persistent, "The persistent flag did not survive the round trip.");
        Assert.AreEqual("""{"ok":true}""", Encoding.UTF8.GetString(received.Body.Span));

        IDictionary<string, object?> headers = received.BasicProperties.Headers!;
        Assert.AreEqual("TaskCreated", RabbitMqHeaders.AsString(headers["EventType"]));
        Assert.AreEqual(3, headers["Attempt"]);
        Assert.AreEqual(9_000_000_000L, headers["Sequence"]);
        Assert.AreEqual(true, headers["Replay"]);
        CollectionAssert.AreEqual(new byte[] { 7, 8 }, (byte[])headers["Raw"]!);
    }
}
