using Microsoft.Extensions.Options;

namespace EF.Messaging.RabbitMq.Tests.Unit;

/// <summary>
/// Contract 5: <c>ValidateOnStart</c> rejects a pool size below one, a prefetch or delivery count below one, and
/// an options instance with no connection source. Pure unit tier - no broker.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class OptionsValidationTests
{
    private static RabbitMqOptions Valid() => new() { ConnectionString = "amqp://user:pass@localhost:5672/" };

    private static ValidateOptionsResult Validate(RabbitMqOptions options, bool connectionRegistered = false) =>
        new RabbitMqOptionsValidator(() => connectionRegistered).Validate(Options.DefaultName, options);

    [TestMethod]
    public void Given_DefaultOptionsWithConnectionString_When_Validated_Then_Succeeds()
    {
        ValidateOptionsResult result = Validate(Valid());

        Assert.IsTrue(result.Succeeded, result.FailureMessage);
    }

    [TestMethod]
    public void Given_RegisteredConnectionAndNoConnectionString_When_Validated_Then_Succeeds()
    {
        ValidateOptionsResult result = Validate(new RabbitMqOptions(), connectionRegistered: true);

        Assert.IsTrue(result.Succeeded, result.FailureMessage);
    }

    [TestMethod]
    public void Given_NoConnectionSource_When_Validated_Then_Fails()
    {
        ValidateOptionsResult result = Validate(new RabbitMqOptions());

        Assert.IsTrue(result.Failed);
        Assert.Contains("No connection source", result.FailureMessage!, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Given_ZeroPoolSize_When_Validated_Then_Fails()
    {
        RabbitMqOptions options = Valid();
        options.PublisherChannelPoolSize = 0;

        ValidateOptionsResult result = Validate(options);

        Assert.IsTrue(result.Failed);
        Assert.Contains(nameof(RabbitMqOptions.PublisherChannelPoolSize), result.FailureMessage!, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Given_ZeroPrefetchCount_When_Validated_Then_Fails()
    {
        RabbitMqOptions options = Valid();
        options.Consumers["q"] = new RabbitMqConsumerOptions { PrefetchCount = 0 };

        ValidateOptionsResult result = Validate(options);

        Assert.IsTrue(result.Failed);
        Assert.Contains(nameof(RabbitMqConsumerOptions.PrefetchCount), result.FailureMessage!, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Given_ZeroMaxDeliveryCount_When_Validated_Then_Fails()
    {
        RabbitMqOptions options = Valid();
        options.Consumers["q"] = new RabbitMqConsumerOptions { MaxDeliveryCount = 0 };

        ValidateOptionsResult result = Validate(options);

        Assert.IsTrue(result.Failed);
        Assert.Contains(nameof(RabbitMqConsumerOptions.MaxDeliveryCount), result.FailureMessage!, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Given_NonPositiveConfirmTimeout_When_Validated_Then_Fails()
    {
        RabbitMqOptions options = Valid();
        options.PublisherConfirmTimeout = TimeSpan.Zero;

        ValidateOptionsResult result = Validate(options);

        Assert.IsTrue(result.Failed);
        Assert.Contains(nameof(RabbitMqOptions.PublisherConfirmTimeout), result.FailureMessage!, StringComparison.Ordinal);
    }
}
