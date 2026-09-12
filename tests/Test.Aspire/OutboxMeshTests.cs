using Aspire.Hosting.Testing;
using EF.Common.Contracts;
using EF.IntegrationTesting.Aspire;
using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TaskFlow.Application.Contracts.Messaging;
using TaskFlow.Application.MessageHandlers.Consumers;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Shared.Events;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Data.Provider;
using Test.Support;
using Test.Support.Hosting;

namespace Test.Aspire;

/// <summary>
/// The whole durable-messaging mesh in one place: an HTTP create stages an outbox row (D-026), the Scheduler
/// dispatches it to the DomainEvents topic, the three filtered subscriptions each hand it to their Function
/// trigger, and every consumer records its ConsumerInbox row (D-029). Replay of the identical envelope adds
/// nothing, and a body that is not an envelope is dead-lettered with a reason rather than retried forever.
/// Aspire tier: needs the full graph (API, Scheduler, Functions, Service Bus emulator, database).
/// </summary>
[TestClass]
[TestCategory("Aspire")]
[DoNotParallelize]
public class OutboxMeshTests
{
    private const string TopicName = "DomainEvents";
    private static readonly TimeSpan MeshBudget = TimeSpan.FromMinutes(3);

    /// <summary>Boots the Aspire graph lazily; teardown is owned by <c>AspireMeshLifecycle</c>.</summary>
    [ClassInitialize]
    public static Task ClassInit(TestContext context) => AspireTestHost.EnsureStartedAsync(context);

    /// <summary>Consumers run in the Functions host on Service Bus and in the Scheduler on RabbitMQ.</summary>
    private static bool UsesRabbitMq => string.Equals(
        Environment.GetEnvironmentVariable("TASKFLOW_MESSAGING_PROVIDER"), "RabbitMq", StringComparison.OrdinalIgnoreCase);

    /// <summary>The dispatcher lives in the Scheduler and the Service Bus consumers live in Functions.</summary>
    [TestInitialize]
    public void TestSetup()
    {
        if (Environment.GetEnvironmentVariable("TASKFLOW_ASPIRE_SCHEDULER_AVAILABLE") != "true")
            Assert.Inconclusive(
                "TASKFLOW_ASPIRE_SCHEDULER_AVAILABLE is not set, so no outbox dispatcher runs in this graph.");

        if (!UsesRabbitMq && !AspireTestHost.EnsureFuncToolAvailable())
            Assert.Inconclusive("Azure Functions Core Tools are unavailable, so no consumer runs in this graph.");
    }

    [TestMethod]
    [Timeout(1_200_000, CooperativeCancellation = true)]
    public async Task Given_TaskCreatedOverHttp_When_MeshDrains_Then_EveryConsumerRecordsItExactlyOnce()
    {
        var ct = TestContext.CancellationToken;
        var client = AspireTestHost.AspireApp!.CreateHttpClient("taskflowapi");

        // Correlate on inbox rows rather than on catching the outbox row mid-flight: the dispatcher polls every
        // second, so a poll-based test would race the hard delete and fail for the wrong reason.
        var before = await InboxMessageIdsAsync(ct);
        var taskItemId = await CreateTaskAsync(client, $"mesh-{Guid.NewGuid():N}", ct);

        var (messageId, consumers) = await WaitForNewInboxGroupAsync(before, expected: 3, ct);
        Assert.AreNotEqual(Guid.Empty, messageId,
            "no message reached all three consumers; the outbox never drained or a subscription never delivered");
        CollectionAssert.AreEquivalent(
            new[] { TaskProjectionConsumer.Name, TaskAiReviewConsumer.Name, TaskWorkflowConsumer.Name },
            consumers,
            "each filtered subscription must deliver the created event to its own consumer");

        await using (var db = CreateContext())
        {
            Assert.AreEqual(0, await db.OutboxMessages.CountAsync(m => m.Id == messageId, ct),
                "a dispatched row is hard-deleted, not left behind");
        }

        if (UsesRabbitMq)
            return; // The replay leg republishes through the Service Bus topic; RabbitMQ has no topic here.

        // Replay: the same MessageId again must be short-circuited by the inbox, not processed a second time.
        var replay = TaskFlowIntegrationEvents.Envelope(
            new TaskItemCreatedEvent(taskItemId, Guid.Empty, "replay"),
            DateTimeOffset.UtcNow,
            correlationId: null,
            id: messageId);
        await PublishAsync(JsonSerializer.Serialize(replay), replay.Type, messageId.ToString(), ct);
        await Task.Delay(TimeSpan.FromSeconds(30), ct);

        await using (var db = CreateContext())
        {
            var rows = await db.ConsumerInbox.AsNoTracking().CountAsync(i => i.MessageId == messageId, ct);
            Assert.AreEqual(3, rows, "a redelivered envelope must not add a second inbox row per consumer");
        }
    }

    [TestMethod]
    [Timeout(1_200_000, CooperativeCancellation = true)]
    public async Task Given_MalformedMessage_When_Consumed_Then_ItIsDeadLetteredWithAReason()
    {
        var ct = TestContext.CancellationToken;
        if (UsesRabbitMq)
            Assert.Inconclusive("RabbitMQ dead-lettering is covered by the package's own broker tests.");

        var messageId = Guid.CreateVersion7().ToString();

        // A body that cannot be an envelope: retrying it would fail identically every time.
        await PublishAsync("{ not an envelope", nameof(TaskItemCreatedEvent), messageId, ct);

        await using var sbClient = new ServiceBusClient(await ServiceBusConnectionStringAsync(ct));
        await using var receiver = sbClient.CreateReceiver(
            TopicName,
            TaskProjectionConsumer.Name,
            new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });

        var deadline = DateTimeOffset.UtcNow + MeshBudget;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var messages = await receiver.ReceiveMessagesAsync(10, TimeSpan.FromSeconds(5), ct);
            var match = messages?.FirstOrDefault(m => m.MessageId == messageId);
            if (match is not null)
            {
                Assert.AreEqual(IntegrationEnvelopeReader.MalformedReason, match.DeadLetterReason);
                return;
            }
        }

        Assert.Fail($"malformed message {messageId} never reached the projection dead-letter queue");
    }

    public TestContext TestContext { get; set; } = null!;

    private static async Task<Guid> CreateTaskAsync(HttpClient client, string title, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync(
            "api/v1/task-items", new DefaultRequest<TaskItemDto> { Item = new TaskItemDto { Title = title } }, ct);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(ct));

        var body = await response.Content.ReadFromJsonAsync<DefaultResponse<TaskItemDto>>(JsonTestOptions.Default, ct);
        Assert.IsNotNull(body?.Item);
        return body.Item.Id!.Value;
    }

    private static async Task<HashSet<Guid>> InboxMessageIdsAsync(CancellationToken ct)
    {
        await using var db = CreateContext();
        return [.. await db.ConsumerInbox.AsNoTracking().Select(i => i.MessageId).ToListAsync(ct)];
    }

    /// <summary>Polls until one message id that was not there before has been recorded by every consumer.</summary>
    private static async Task<(Guid MessageId, List<string> Consumers)> WaitForNewInboxGroupAsync(
        HashSet<Guid> before, int expected, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + MeshBudget;

        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var db = CreateContext();
            var rows = await db.ConsumerInbox.AsNoTracking()
                .Select(i => new { i.MessageId, i.Consumer })
                .ToListAsync(ct);

            var group = rows
                .Where(r => !before.Contains(r.MessageId))
                .GroupBy(r => r.MessageId)
                .FirstOrDefault(g => g.Count() >= expected);

            if (group is not null)
                return (group.Key, [.. group.Select(r => r.Consumer)]);

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        return (Guid.Empty, []);
    }

    private static async Task PublishAsync(string body, string eventType, string messageId, CancellationToken ct)
    {
        await using var client = new ServiceBusClient(await ServiceBusConnectionStringAsync(ct));
        await using var sender = client.CreateSender(TopicName);

        var message = new ServiceBusMessage(Encoding.UTF8.GetBytes(body))
        {
            ContentType = "application/json",
            MessageId = messageId,
            Subject = eventType
        };
        message.ApplicationProperties["EventType"] = eventType;
        await sender.SendMessageAsync(message, ct);
    }

    private static Task<string> ServiceBusConnectionStringAsync(CancellationToken ct) =>
        AspireTestHost.AspireApp!.GetRequiredConnectionStringAsync("ServiceBus1", AspireTestHost.DefaultTimeout, ct);

    private static TaskFlowDbContextTrxn CreateContext() =>
        new(new DbContextOptionsBuilder<TaskFlowDbContextTrxn>()
            .UseTaskFlowProvider(new TaskFlowProviderOptions(
                TestDbProvider.Current,
                AspireTestHost.ConnectionString,
                TaskFlowDbContextBase.MigrationHistoryTable,
                TaskFlowDbContextBase.SchemaName))
            .Options)
        { AuditId = "mesh-test" };
}
