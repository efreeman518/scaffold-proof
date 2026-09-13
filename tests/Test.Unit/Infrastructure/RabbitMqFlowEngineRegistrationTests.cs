using EF.FlowEngine.Clients;
using EF.FlowEngine.Model;
using EF.Messaging.RabbitMq;
using Moq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Text.Json;
using TaskFlow.Bootstrapper;
using TaskFlow.Infrastructure.Messaging.RabbitMq;
using TaskFlow.Observability.Tracing;

namespace Test.Unit.Infrastructure;

/// <summary>Proves NonAzure workflow message nodes use the established RabbitMQ transport contract.</summary>
[TestClass]
[TestCategory("Unit")]
public sealed class RabbitMqFlowEngineRegistrationTests
{
    [TestMethod]
    public async Task NonAzureApplicationServices_RegisterIntegrationEventsClient()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hosting:Lane"] = "NonAzure",
                [$"{RabbitMqRegistration.OptionsSection}:ConnectionString"] = "amqp://guest:guest@localhost:5672/"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTaskFlowRabbitMqMessaging(config);
        services.RegisterApplicationServices(config);

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetServices<EF.FlowEngine.Abstractions.IFlowClient>()
            .Single(value => value.ClientRef == "integration-events");

        Assert.IsInstanceOfType<DelegatingMessageClient>(client);
    }

    [TestMethod]
    public void IntegrationEventsClient_MapsRoutingMetadataAndInjectsProducerTrace()
    {
        using var listener = Listen(out var started);
        var body = JsonSerializer.SerializeToElement(new { taskId = "task-42" });
        var request = new MessageRequest
        {
            Subject = "taskitem.triaged",
            Body = body,
            CorrelationId = "correlation-42",
            IdempotencyKey = "task-42-triage-event",
            Properties = new Dictionary<string, string> { ["tenant-id"] = "tenant-7" }
        };

        var message = RegisterServices.CreateRabbitMqFlowEngineMessage(
            request, request.IdempotencyKey, out var publishActivity);
        using (publishActivity)
        {
            Assert.AreEqual("taskitem.triaged", message.RoutingKey);
            Assert.AreEqual("task-42-triage-event", message.MessageId);
            Assert.AreEqual("correlation-42", message.CorrelationId);
            Assert.AreEqual("application/json", message.ContentType);
            Assert.IsTrue(message.Persistent);
            Assert.IsNotNull(message.Headers);
            Assert.AreEqual("tenant-7", message.Headers["tenant-id"]);
            Assert.IsTrue(message.Headers.TryGetValue("traceparent", out var traceparent));
            StringAssert.Contains((string?)traceparent, publishActivity!.TraceId.ToHexString());
            StringAssert.Contains((string?)traceparent, publishActivity.SpanId.ToHexString());
            CollectionAssert.AreEqual(JsonSerializer.SerializeToUtf8Bytes(body), message.Body.ToArray());
        }

        var published = started.Single(activity =>
            activity.Kind == ActivityKind.Producer
            && (string?)activity.GetTagItem("messaging.message.id") == request.IdempotencyKey);
        Assert.AreEqual("taskitem.triaged publish", published.OperationName);
        Assert.AreEqual(MessagingTrace.RabbitMqSystem, published.GetTagItem("messaging.system"));
        Assert.AreEqual(TaskFlowRabbitMqTopology.Exchange, published.GetTagItem("messaging.destination.name"));
    }

    [TestMethod]
    public async Task IntegrationEventsClient_RejectsUnsupportedRequestReplyBeforePublishing()
    {
        var multiplexer = new Mock<IRabbitMqConnectionMultiplexer>(MockBehavior.Strict);
        var client = RegisterServices.CreateRabbitMqFlowEngineMessageClient(multiplexer.Object);

        var exception = await Assert.ThrowsExactlyAsync<NotSupportedException>(() => client.SendAsync(
            new MessageRequest
            {
                Subject = "request",
                Body = JsonSerializer.SerializeToElement(new { }),
                DeliveryMode = MessageDeliveryMode.RequestReply
            },
            TestContext.CancellationToken));

        StringAssert.Contains(exception.Message, "fire-and-forget");
    }

    private static ActivityListener Listen(out List<Activity> started)
    {
        var captured = new List<Activity>();
        started = captured;
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TaskFlowActivitySources.MessagingName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = captured.Add
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    public TestContext TestContext { get; set; } = null!;
}
