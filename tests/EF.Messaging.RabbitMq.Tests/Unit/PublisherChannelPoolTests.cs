using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;

namespace EF.Messaging.RabbitMq.Tests.Unit;

/// <summary>
/// Contract 1 pool bookkeeping against a faked <see cref="IConnection"/>: the pool never creates more channels
/// than its size, reuses an open channel, and discards a channel the broker closed. Pure unit tier - no broker.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class PublisherChannelPoolTests
{
    private static Mock<IChannel> ChannelMock(Func<bool> isOpen)
    {
        Mock<IChannel> channel = new();
        channel.SetupGet(c => c.IsOpen).Returns(isOpen);
        channel.SetupGet(c => c.ChannelNumber).Returns(1);
        channel.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
        return channel;
    }

    private static RabbitMqConnectionMultiplexer CreateMultiplexer(IConnection connection, int poolSize, RabbitMqMetrics metrics)
    {
        ServiceProvider services = new ServiceCollection().AddSingleton(connection).BuildServiceProvider();
        return new RabbitMqConnectionMultiplexer(
            new TestOptionsMonitor<RabbitMqOptions>(new RabbitMqOptions { PublisherChannelPoolSize = poolSize }),
            services,
            metrics,
            NullLogger<RabbitMqConnectionMultiplexer>.Instance);
    }

    [TestMethod]
    public async Task Given_PoolSizeTwo_When_ThirdRentRequested_Then_ItWaitsAndReusesAReturnedChannel()
    {
        int created = 0;
        Mock<IConnection> connection = new();
        connection
            .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                Interlocked.Increment(ref created);
                return ChannelMock(() => true).Object;
            });

        using RabbitMqMetrics metrics = new();
        await using RabbitMqConnectionMultiplexer multiplexer = CreateMultiplexer(connection.Object, poolSize: 2, metrics);

        PooledChannel first = await multiplexer.RentPublisherChannelAsync(TestContext.CancellationToken);
        PooledChannel second = await multiplexer.RentPublisherChannelAsync(TestContext.CancellationToken);

        ValueTask<PooledChannel> third = multiplexer.RentPublisherChannelAsync(TestContext.CancellationToken);
        Assert.IsFalse(third.IsCompleted, "The third rent must wait while the pool of two is exhausted.");
        Assert.AreEqual(2, Volatile.Read(ref created));
        Assert.AreEqual(2, metrics.PublisherPoolRented);

        first.Dispose();
        PooledChannel reused = await third;

        Assert.AreEqual(2, Volatile.Read(ref created), "A returned open channel must be reused, not replaced.");
        Assert.AreSame(first.Channel, reused.Channel);

        reused.Dispose();
        second.Dispose();
        Assert.AreEqual(0, metrics.PublisherPoolRented);
    }

    [TestMethod]
    public async Task Given_ChannelClosedByTheBroker_When_Returned_Then_ItIsDisposedAndNotReused()
    {
        bool firstChannelOpen = true;
        Mock<IChannel> faulted = ChannelMock(() => firstChannelOpen);
        Mock<IChannel> replacement = ChannelMock(() => true);
        Queue<IChannel> channels = new([faulted.Object, replacement.Object]);

        Mock<IConnection> connection = new();
        connection
            .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(channels.Dequeue);

        using RabbitMqMetrics metrics = new();
        await using RabbitMqConnectionMultiplexer multiplexer = CreateMultiplexer(connection.Object, poolSize: 1, metrics);

        PooledChannel rented = await multiplexer.RentPublisherChannelAsync(TestContext.CancellationToken);
        Assert.AreSame(faulted.Object, rented.Channel);

        firstChannelOpen = false;
        rented.Dispose();

        PooledChannel next = await multiplexer.RentPublisherChannelAsync(TestContext.CancellationToken);

        Assert.AreSame(replacement.Object, next.Channel, "A closed channel must never be handed out again.");
        faulted.Verify(c => c.DisposeAsync(), Times.Once);
        next.Dispose();
    }

    /// <summary>MSTest injects the per-test context, which carries the cancellation token used above.</summary>
    public TestContext TestContext { get; set; } = null!;
}
