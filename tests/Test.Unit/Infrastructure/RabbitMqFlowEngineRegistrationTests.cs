using EF.FlowEngine.Clients;
using EF.FlowEngine.Model;
using EF.Messaging.RabbitMq;
using EF.Messaging.Tracing;
using Moq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using TaskFlow.Bootstrapper;
using TaskFlow.Infrastructure.Messaging.RabbitMq;

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
        Assert.AreEqual($"send {TaskFlowRabbitMqTopology.Exchange}", published.OperationName);
        Assert.AreEqual(MessagingActivitySource.RabbitMqSystem, published.GetTagItem("messaging.system"));
        Assert.AreEqual(TaskFlowRabbitMqTopology.Exchange, published.GetTagItem("messaging.destination.name"));
    }

    [TestMethod]
    public async Task IntegrationEventsClient_RejectsUnsupportedRequestReplyBeforePublishing()
    {
        var publisher = new Mock<IRabbitMqPublisher>(MockBehavior.Strict);
        var client = RegisterServices.CreateRabbitMqFlowEngineMessageClient(publisher.Object);

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

    /// <summary>
    /// Workflow messages go through the package publisher (pooled confirm channel, mandatory:false,
    /// PublisherConfirmTimeout) to the TaskFlow exchange; the producer span stays open until the confirm.
    /// </summary>
    [TestMethod]
    public async Task IntegrationEventsClient_PublishesThroughThePackagePublisher()
    {
        using var listener = Listen(out var started);
        var publisher = new Mock<IRabbitMqPublisher>(MockBehavior.Strict);
        RabbitMqMessage? published = null;
        var spanOpenAtPublish = false;
        publisher
            .Setup(p => p.PublishAsync(TaskFlowRabbitMqTopology.Exchange, It.IsAny<RabbitMqMessage>(), It.IsAny<CancellationToken>()))
            .Callback<string, RabbitMqMessage, CancellationToken>((_, message, _) =>
            {
                published = message;
                spanOpenAtPublish = started.Any(a =>
                    (string?)a.GetTagItem("messaging.message.id") == "wf-publish-1" && a.Duration == TimeSpan.Zero);
            })
            .Returns(Task.CompletedTask);
        var client = RegisterServices.CreateRabbitMqFlowEngineMessageClient(publisher.Object);

        var result = await client.SendAsync(
            new MessageRequest
            {
                Subject = "taskitem.triaged",
                Body = JsonSerializer.SerializeToElement(new { taskId = "task-42" }),
                IdempotencyKey = "wf-publish-1"
            },
            TestContext.CancellationToken);

        Assert.IsTrue(result.Sent);
        Assert.AreEqual("wf-publish-1", result.MessageId);
        Assert.IsNotNull(published);
        Assert.AreEqual("taskitem.triaged", published.RoutingKey);
        Assert.IsTrue(spanOpenAtPublish, "the producer span must cover the confirmed publish, not end before it");
        publisher.VerifyAll();
    }

    /// <summary>
    /// A confirm timeout or broker refusal surfaces as the package's RabbitMqPublishException carrying the primary
    /// failure, so the workflow node fails instead of reporting a message it never delivered.
    /// </summary>
    [TestMethod]
    public async Task IntegrationEventsClient_BrokerFailure_PropagatesThePublishException()
    {
        var primary = new InvalidOperationException("broker channel closed");
        var publisher = new Mock<IRabbitMqPublisher>(MockBehavior.Strict);
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<RabbitMqMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RabbitMqPublishException("not confirmed", [0], primary));
        var client = RegisterServices.CreateRabbitMqFlowEngineMessageClient(publisher.Object);

        var observed = await Assert.ThrowsExactlyAsync<RabbitMqPublishException>(() => client.SendAsync(
            new MessageRequest { Subject = "taskitem.triaged", Body = JsonSerializer.SerializeToElement(new { }) },
            TestContext.CancellationToken));

        Assert.AreSame(primary, observed.InnerException);
    }

    private static ActivityListener Listen(out ConcurrentQueue<Activity> started)
    {
        // Thread safe: tests run in parallel and every live listener sees every activity of the source.
        var captured = new ConcurrentQueue<Activity>();
        started = captured;
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == MessagingActivitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = captured.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    public TestContext TestContext { get; set; } = null!;
}
