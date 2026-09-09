using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace EF.Messaging.RabbitMq;

/// <summary>
/// Validates <see cref="RabbitMqOptions"/> at host startup (<c>ValidateOnStart</c>): pool size, every consumer's
/// prefetch and delivery bounds, and the presence of a connection source (a registered
/// <see cref="IConnection"/> or a configured connection string).
/// </summary>
internal sealed class RabbitMqOptionsValidator(Func<bool> connectionRegistered) : IValidateOptions<RabbitMqOptions>
{
    public ValidateOptionsResult Validate(string? name, RabbitMqOptions options)
    {
        List<string> failures = [];

        if (options.PublisherChannelPoolSize < 1)
            failures.Add($"{nameof(RabbitMqOptions.PublisherChannelPoolSize)} must be at least 1.");

        if (options.PublisherConfirmTimeout <= TimeSpan.Zero)
            failures.Add($"{nameof(RabbitMqOptions.PublisherConfirmTimeout)} must be greater than zero.");

        foreach ((string queue, RabbitMqConsumerOptions consumer) in options.Consumers)
        {
            if (consumer.PrefetchCount < 1)
                failures.Add($"Consumers[{queue}].{nameof(RabbitMqConsumerOptions.PrefetchCount)} must be at least 1.");

            if (consumer.MaxDeliveryCount < 1)
                failures.Add($"Consumers[{queue}].{nameof(RabbitMqConsumerOptions.MaxDeliveryCount)} must be at least 1.");
        }

        if (string.IsNullOrWhiteSpace(options.ConnectionString) && !connectionRegistered())
            failures.Add($"No connection source: register an {nameof(IConnection)} or set {nameof(RabbitMqOptions.ConnectionString)}.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
