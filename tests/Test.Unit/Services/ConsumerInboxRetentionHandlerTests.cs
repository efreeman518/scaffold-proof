using EF.Messaging;
using Microsoft.Extensions.Configuration;
using Moq;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Scheduler.Handlers.Retention;

namespace Test.Unit.Services;

/// <summary>
/// Validates <see cref="ConsumerInboxRetentionHandler"/>: one run purges the consumer inbox and the D-074
/// idempotency-key mappings, each at its own configured age.
/// Pure-unit tier (Moq only): the stores are mocks.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class ConsumerInboxRetentionHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public TestContext TestContext { get; set; } = null!;

    /// <summary>The idempotency-key purge runs with its own retention (3 days here), beside the inbox purge (10 days).</summary>
    [TestMethod]
    public async Task HandleAsync_PurgesIdempotencyKeysOlderThanTheirRetention()
    {
        var inbox = new Mock<IInboxStore>();
        var keys = new Mock<IIdempotencyKeyRepository>();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Scheduling:Retention:ConsumerInboxDays"] = "10",
            ["Scheduling:Retention:IdempotencyKeyDays"] = "3"
        }).Build();

        await new ConsumerInboxRetentionHandler(inbox.Object, keys.Object, SchedulerTestTelemetry.Create(), new FixedTimeProvider(Now), config)
            .HandleAsync(TestContext.CancellationToken);

        keys.Verify(k => k.PurgeAsync(Now.AddDays(-3), It.IsAny<CancellationToken>()), Times.Once);
        inbox.Verify(i => i.PurgeAsync(Now.AddDays(-10), It.IsAny<CancellationToken>()), Times.Once);
    }
}
