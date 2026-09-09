namespace EF.Messaging.RabbitMq;

/// <summary>
/// Root options for the RabbitMQ transport, bound from configuration section <c>Messaging:RabbitMq</c> by default.
/// </summary>
public sealed class RabbitMqOptions
{
    /// <summary>
    /// AMQP connection string (<c>amqp(s)://user:pass@host:port/vhost</c>). Ignored when an
    /// <see cref="global::RabbitMQ.Client.IConnection"/> is already registered in the container
    /// (for example by <c>Aspire.RabbitMQ.Client</c>).
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>Connection name reported to the broker and shown in the management UI.</summary>
    public string ClientProvidedName { get; set; } = "ef-messaging";

    /// <summary>
    /// Upper bound on concurrently rented publisher channels. Renting waits asynchronously when the pool is exhausted.
    /// </summary>
    public int PublisherChannelPoolSize { get; set; } = 8;

    /// <summary>Time a publish batch may wait for broker confirms before it is reported as unconfirmed.</summary>
    public TimeSpan PublisherConfirmTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Enables the client's automatic connection recovery.</summary>
    public bool AutomaticRecoveryEnabled { get; set; } = true;

    /// <summary>Interval between automatic recovery attempts.</summary>
    public TimeSpan NetworkRecoveryInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Per-queue consumer options, keyed by queue name.</summary>
    public Dictionary<string, RabbitMqConsumerOptions> Consumers { get; set; } = [];
}

/// <summary>Per-queue consumer options.</summary>
public sealed class RabbitMqConsumerOptions
{
    /// <summary>
    /// Unacknowledged deliveries the broker may have outstanding on the consumer channel
    /// (<c>BasicQos(0, PrefetchCount, global: false)</c>). Also the consumer's dispatch concurrency.
    /// </summary>
    public ushort PrefetchCount { get; set; } = 16;

    /// <summary>Delivery attempts allowed before a retried message is rejected to the queue's dead-letter exchange.</summary>
    public int MaxDeliveryCount { get; set; } = 5;

    /// <summary>When false, <c>AddRabbitMqConsumer</c> registers the handler but starts no hosted service.</summary>
    public bool Enabled { get; set; } = true;
}
