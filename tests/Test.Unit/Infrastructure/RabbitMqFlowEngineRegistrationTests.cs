using EF.FlowEngine.Clients;
using EF.FlowEngine.Model;
using EF.Messaging.RabbitMq;
using Moq;
using System.Text.Json;
using TaskFlow.Bootstrapper;
using TaskFlow.Infrastructure.Messaging.RabbitMq;

namespace Test.Unit.Infrastructure;

/// <summary>Proves NonAzure workflow message nodes use the established confirmed RabbitMQ publisher.</summary>
[TestClass]
[TestCategory("Unit")]
public sealed class RabbitMqFlowEngineRegistrationTests
{
    [TestMethod]
    public async Task IntegrationEventsClient_PublishesToTaskFlowExchangeWithWorkflowRoutingMetadata()
    {
        RabbitMqMessage? published = null;
        string? exchange = null;
        var publisher = new Mock<IRabbitMqPublisher>();
        publisher
            .Setup(value => value.PublishAsync(
                It.IsAny<string>(),
                It.IsAny<RabbitMqMessage>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, RabbitMqMessage, CancellationToken>((target, message, _) =>
            {
                exchange = target;
                published = message;
            })
            .Returns(Task.CompletedTask);

        var client = RegisterServices.CreateRabbitMqFlowEngineMessageClient(publisher.Object);
        var body = JsonSerializer.SerializeToElement(new { taskId = "task-42" });

        var result = await client.SendAsync(new MessageRequest
        {
            Subject = "taskitem.triaged",
            Body = body,
            CorrelationId = "correlation-42",
            IdempotencyKey = "task-42-triage-event",
            Properties = new Dictionary<string, string> { ["tenant-id"] = "tenant-7" }
        }, TestContext.CancellationToken);

        Assert.IsTrue(result.Sent);
        Assert.AreEqual("task-42-triage-event", result.MessageId);
        Assert.AreEqual(DecisionOutcome.Match, result.Outcome);
        Assert.AreEqual(TaskFlowRabbitMqTopology.Exchange, exchange);
        Assert.IsNotNull(published);
        Assert.AreEqual("taskitem.triaged", published.RoutingKey);
        Assert.AreEqual("task-42-triage-event", published.MessageId);
        Assert.AreEqual("correlation-42", published.CorrelationId);
        Assert.AreEqual("application/json", published.ContentType);
        Assert.IsTrue(published.Persistent);
        Assert.AreEqual("tenant-7", published.Headers!["tenant-id"]);
        CollectionAssert.AreEqual(JsonSerializer.SerializeToUtf8Bytes(body), published.Body.ToArray());
        publisher.VerifyAll();
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

    public TestContext TestContext { get; set; } = null!;
}
