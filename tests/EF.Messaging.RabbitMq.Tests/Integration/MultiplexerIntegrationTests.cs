using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using System.Collections.Concurrent;
using System.Text;
using Testcontainers.RabbitMq;

namespace EF.Messaging.RabbitMq.Tests.Integration;

/// <summary>
/// Contract 1 against a real broker: the publisher pool is bounded, a channel the broker closed is never handed
/// out again, and an <see cref="IConnection"/> registered by another integration is reused rather than duplicated.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class MultiplexerIntegrationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Given_PoolSizeEight_When_FiftyRentsRunConcurrently_Then_AtMostEightChannelsExist()
    {
        CancellationToken ct = TestContext.CancellationToken;
        RabbitMqContainer broker = await RabbitMqFixture.EnsureStartedAsync(ct);
        string clientName = $"pool-{Guid.NewGuid():N}";

        await using ServiceProvider provider = TestServices.Build(broker, clientName,
            [new("Messaging:RabbitMq:PublisherChannelPoolSize", "8")]);
        IRabbitMqConnectionMultiplexer multiplexer = provider.GetRequiredService<IRabbitMqConnectionMultiplexer>();

        ConcurrentDictionary<IChannel, byte> distinctChannels = new();
        await Task.WhenAll(Enumerable.Range(0, 50).Select(async _ =>
        {
            using PooledChannel rented = await multiplexer.RentPublisherChannelAsync(ct);
            distinctChannels.TryAdd(rented.Channel, 0);
            await Task.Delay(20, ct);
        }));

        Assert.IsLessThanOrEqualTo(8, distinctChannels.Count,
            $"The pool created {distinctChannels.Count} channels for a pool size of 8.");

        using RabbitMqManagement management = RabbitMqFixture.Management(broker);
        int channels = await Poll.UntilAsync(
            token => management.ChannelCountAsync(clientName, token),
            count => count > 0,
            TimeSpan.FromSeconds(30),
            "the management API to report the publisher channels",
            ct);

        Assert.IsLessThanOrEqualTo(8, channels, "The broker sees more open channels than the pool size.");
    }

    [TestMethod]
    public async Task Given_APublishToAMissingExchange_When_TheChannelIsReturned_Then_ItIsReplaced()
    {
        CancellationToken ct = TestContext.CancellationToken;
        RabbitMqContainer broker = await RabbitMqFixture.EnsureStartedAsync(ct);

        await using ServiceProvider provider = TestServices.Build(broker, $"faulted-{Guid.NewGuid():N}",
            [new("Messaging:RabbitMq:PublisherChannelPoolSize", "1")]);
        IRabbitMqConnectionMultiplexer multiplexer = provider.GetRequiredService<IRabbitMqConnectionMultiplexer>();
        IRabbitMqPublisher publisher = provider.GetRequiredService<IRabbitMqPublisher>();

        IChannel first;
        using (PooledChannel rented = await multiplexer.RentPublisherChannelAsync(ct))
            first = rented.Channel;

        RabbitMqMessage message = new(Encoding.UTF8.GetBytes("{}"), "key", Guid.NewGuid().ToString());
        await Assert.ThrowsExactlyAsync<RabbitMqPublishException>(
            () => publisher.PublishAsync($"missing-{Guid.NewGuid():N}", message, ct));

        using PooledChannel next = await multiplexer.RentPublisherChannelAsync(ct);

        Assert.AreNotSame(first, next.Channel, "The channel the broker closed was handed out again.");
        Assert.IsTrue(next.Channel.IsOpen);
    }

    [TestMethod]
    public async Task Given_AnIConnectionAlreadyRegistered_When_TheMultiplexerConnects_Then_NoSecondConnectionIsOpened()
    {
        CancellationToken ct = TestContext.CancellationToken;
        RabbitMqContainer broker = await RabbitMqFixture.EnsureStartedAsync(ct);
        string clientName = $"registered-{Guid.NewGuid():N}";

        ConnectionFactory factory = new() { Uri = new Uri(broker.GetConnectionString()), ClientProvidedName = clientName };
        await using IConnection registered = await factory.CreateConnectionAsync(ct);

        // The connection string here is deliberately unusable: nothing may fall back to creating a connection.
        await using ServiceProvider provider = TestServices.Build(broker, $"unused-{Guid.NewGuid():N}",
            [new("Messaging:RabbitMq:ConnectionString", "amqp://nobody:nobody@127.0.0.1:1/")],
            services => services.AddSingleton(registered));

        IRabbitMqConnectionMultiplexer multiplexer = provider.GetRequiredService<IRabbitMqConnectionMultiplexer>();
        IConnection resolved = await multiplexer.GetConnectionAsync(ct);

        Assert.AreSame(registered, resolved);

        using RabbitMqManagement management = RabbitMqFixture.Management(broker);
        int connections = await Poll.UntilAsync(
            token => management.ConnectionCountAsync(clientName, token),
            count => count > 0,
            TimeSpan.FromSeconds(30),
            "the management API to report the registered connection",
            ct);

        Assert.AreEqual(1, connections, "A second connection was opened alongside the registered one.");
    }
}
