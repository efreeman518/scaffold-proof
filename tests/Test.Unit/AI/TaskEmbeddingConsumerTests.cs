using EF.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using TaskFlow.Application.Contracts.Messaging;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.MessageHandlers.Consumers;
using TaskFlow.Domain.Shared.Events;
using TaskFlow.Observability.Meters;
using Test.Support;

namespace Test.Unit.AI;

/// <summary>
/// D-040 embedding consumer decision logic: what it embeds, what it records with the vector, and what it does
/// when the task is already gone. All three are choices an integration test would only reach through a
/// container and a live model, so they are asserted directly against fakes.
/// Pure-unit tier (in-memory fakes): no database, no embedding service.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class TaskEmbeddingConsumerTests
{
    [TestMethod]
    public async Task Consume_EmbedsTitleAndDescription_ThenUpsertsWithTheModelThatProducedIt()
    {
        var ct = TestContext.CancellationToken;
        var taskItemId = Guid.CreateVersion7();
        var repo = new FakeEmbeddingRepository
        {
            Source = new TaskEmbeddingSource("Ship the release", "Cut a tag and publish the notes")
        };
        var generator = new FakeEmbeddingGenerator();
        var consumer = NewConsumer(repo, generator);

        await consumer.HandleAsync(Envelope(new TaskItemCreatedEvent(taskItemId, TestConstants.TenantId, "Ship the release")), ct);

        Assert.AreEqual(1, generator.Requests.Count);
        StringAssert.Contains(generator.Requests[0], "Ship the release");
        StringAssert.Contains(generator.Requests[0], "Cut a tag and publish the notes");

        Assert.IsNotNull(repo.Upserted);
        Assert.AreEqual(taskItemId, repo.Upserted.Value.TaskItemId);
        Assert.AreEqual(TestConstants.TenantId, repo.Upserted.Value.TenantId);
        Assert.AreEqual(FakeEmbeddingGenerator.ModelName, repo.Upserted.Value.ModelId);
        Assert.AreEqual(FakeEmbeddingGenerator.Dimensions, repo.Upserted.Value.Length);
        Assert.IsNull(repo.Deleted);
    }

    [TestMethod]
    public async Task Consume_ContentChangedForAMissingTask_DeletesTheRowInsteadOfEmbedding()
    {
        var ct = TestContext.CancellationToken;
        var taskItemId = Guid.CreateVersion7();
        var repo = new FakeEmbeddingRepository { Source = null };
        var generator = new FakeEmbeddingGenerator();
        var consumer = NewConsumer(repo, generator);

        await consumer.HandleAsync(
            Envelope(new TaskItemContentChangedEvent(taskItemId, TestConstants.TenantId, DateTimeOffset.UtcNow)), ct);

        Assert.AreEqual(0, generator.Requests.Count, "a task that no longer exists must not cost a model call");
        Assert.IsNull(repo.Upserted);
        Assert.AreEqual((TestConstants.TenantId, taskItemId), repo.Deleted);
    }

    /// <summary>Status-class events are not this consumer's business: they never change the embeddable text.</summary>
    [TestMethod]
    public void Handles_OnlyTheTwoContentEvents()
    {
        var consumer = NewConsumer(new FakeEmbeddingRepository(), new FakeEmbeddingGenerator());

        Assert.IsTrue(consumer.Handles(nameof(TaskItemCreatedEvent)));
        Assert.IsTrue(consumer.Handles(nameof(TaskItemContentChangedEvent)));
        Assert.IsFalse(consumer.Handles(nameof(TaskItemStatusChangedEvent)));
        Assert.IsFalse(consumer.Handles(nameof(TaskItemCompletedEvent)));
        Assert.IsFalse(consumer.Handles(nameof(TaskItemRescheduledEvent)));
    }

    public TestContext TestContext { get; set; } = null!;

    private static TaskEmbeddingConsumer NewConsumer(
        FakeEmbeddingRepository repo, FakeEmbeddingGenerator generator) =>
        new(new FakeInboxStore(), repo, generator, new MessagingMetrics(),
            NullLogger<TaskEmbeddingConsumer>.Instance);

    private static IntegrationEventEnvelope Envelope(TaskFlow.Domain.Shared.IDomainEvent domainEvent) =>
        TaskFlowIntegrationEvents.Envelope(domainEvent, DateTimeOffset.UtcNow, correlationId: null);

    private sealed class FakeInboxStore : IInboxStore
    {
        private readonly HashSet<(string, Guid)> _claims = [];

        public Task<bool> TryClaimAsync(string consumer, Guid messageId, CancellationToken ct = default)
            => Task.FromResult(_claims.Add((consumer, messageId)));

        public Task ReleaseAsync(string consumer, Guid messageId, CancellationToken ct = default)
        {
            _claims.Remove((consumer, messageId));
            return Task.CompletedTask;
        }

        public Task<int> PurgeProcessedAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default)
            => Task.FromResult(0);
    }

    private sealed class FakeEmbeddingRepository : ITaskEmbeddingRepository
    {
        public TaskEmbeddingSource? Source { get; set; }

        public (Guid TenantId, Guid TaskItemId, string ModelId, int Length)? Upserted { get; private set; }

        public (Guid TenantId, Guid TaskItemId)? Deleted { get; private set; }

        public Task<TaskEmbeddingSource?> GetSourceAsync(Guid tenantId, Guid taskItemId, CancellationToken ct = default)
            => Task.FromResult(Source);

        public Task UpsertAsync(
            Guid tenantId, Guid taskItemId, ReadOnlyMemory<float> embedding, string modelId,
            DateTimeOffset updatedUtc, CancellationToken ct = default)
        {
            Upserted = (tenantId, taskItemId, modelId, embedding.Length);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid tenantId, Guid taskItemId, CancellationToken ct = default)
        {
            Deleted = (tenantId, taskItemId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TaskEmbeddingMatch>> SearchNearestAsync(
            Guid tenantId, ReadOnlyMemory<float> query, int take, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TaskEmbeddingMatch>>([]);
    }

    /// <summary>Deterministic stand-in: the vector is a hash of the input, so equal text embeds equally.</summary>
    private sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public const string ModelName = "fake-embed-3";
        public const int Dimensions = 8;

        public List<string> Requests { get; } = [];

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var results = new List<Embedding<float>>();
            foreach (var value in values)
            {
                Requests.Add(value);
                var seed = value.Aggregate(17, (acc, c) => acc * 31 + c);
                var vector = new float[Dimensions];
                for (var i = 0; i < Dimensions; i++) vector[i] = ((seed >> i) & 1) == 1 ? 1f : 0f;
                results.Add(new Embedding<float>(vector) { ModelId = ModelName });
            }

            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(results));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
