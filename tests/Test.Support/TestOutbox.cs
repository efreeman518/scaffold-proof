using EF.Data.Outbox;
using EF.Messaging;
using Microsoft.Extensions.Options;
using TaskFlow.Application.Contracts.Messaging;

namespace Test.Support;

/// <summary>
/// The outbox staging configuration the hosts register (D-026, RegisterServices.Database), built without DI for
/// contexts that tests create directly: EF.Data.Outbox's interceptor over TaskFlow's mapper and serializer options.
/// </summary>
public static class TestOutbox
{
    /// <summary>Staging options as the hosts configure them.</summary>
    public static IOptions<OutboxOptions> Options { get; } = Microsoft.Extensions.Options.Options.Create(new OutboxOptions
    {
        DefaultDestination = TaskFlowIntegrationEvents.Destination,
        SerializerOptions = TaskFlowMessagingJsonContext.Default.Options
    });

    /// <summary>The staging interceptor the hosts add to the write context.</summary>
    /// <param name="clock">Clock for the rows' due times; the system clock when null.</param>
    public static OutboxStagingInterceptor Interceptor(TimeProvider? clock = null) =>
        new(new TaskFlowOutboxEventMapper(), Options, new MessagingMetrics(), clock);
}
