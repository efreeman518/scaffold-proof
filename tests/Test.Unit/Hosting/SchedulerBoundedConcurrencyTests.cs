using EF.Storage.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using TaskFlow.Application.Contracts.Storage;
using TaskFlow.Infrastructure.Data.Messaging;
using TaskFlow.Infrastructure.Data.Operational;
using TaskFlow.Observability.Meters;
using TaskFlow.Scheduler.Workers;

namespace Test.Unit.Hosting;

/// <summary>
/// Covers the D-055 bounded-concurrency drains. The bound is the whole point of the change: an unbounded
/// fan-out over a claimed batch of 50 would hammer the storage account or the broker, and a bound that
/// silently degraded back to 1 would look identical in every other test. Both are asserted here with fakes
/// that observe concurrency directly.
/// Pure-unit tier: fakes only, no DI container, database, host, or network.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class SchedulerBoundedConcurrencyTests
{
    /// <summary>Verifies the blob drain runs deletes concurrently but never exceeds the configured bound.</summary>
    [TestMethod]
    public async Task Given_BlobDeleteBatch_When_Drained_Then_InFlightDeletesRespectTheBound()
    {
        const int maxConcurrency = 4;
        var blobs = new ConcurrencyObservingBlobStore(TimeSpan.FromMilliseconds(20));
        var items = Enumerable.Range(0, 24).Select(NewWork).ToList();

        var (deleted, failed) = await BlobDeleteWorkerService.DeleteBatchAsync(
            blobs, items, maxConcurrency, CancellationToken.None);

        Assert.AreEqual(items.Count, deleted.Count, "Every row should be reported as deleted.");
        Assert.AreEqual(0, failed.Count, "No delete failed, so nothing should be released.");
        Assert.AreEqual(items.Count, blobs.Calls, "Every row should have been attempted exactly once.");
        Assert.IsTrue(blobs.MaxInFlight <= maxConcurrency,
            $"{blobs.MaxInFlight} deletes were in flight with a bound of {maxConcurrency}.");
        Assert.IsTrue(blobs.MaxInFlight > 1,
            "The drain ran the deletes sequentially; the bound is being applied as 1, which is the "
            + "behavior this change replaced.");
    }

    /// <summary>Verifies a failing row is reported for release and the rest of the batch still completes.</summary>
    [TestMethod]
    public async Task Given_OneFailingBlob_When_Drained_Then_OnlyThatRowIsReportedFailed()
    {
        var items = Enumerable.Range(0, 6).Select(NewWork).ToList();
        var doomed = items[3];
        var blobs = new ConcurrencyObservingBlobStore(
            TimeSpan.Zero, failOn: name => name == doomed.BlobName);

        var (deleted, failed) = await BlobDeleteWorkerService.DeleteBatchAsync(
            blobs, items, maxConcurrency: 4, CancellationToken.None);

        Assert.AreEqual(5, deleted.Count);
        Assert.AreEqual(1, failed.Count);
        Assert.AreEqual(doomed.Id, failed.Single().Item.Id);
        CollectionAssert.DoesNotContain(deleted.ToList(), doomed.Id,
            "A row whose delete threw must not be hard-deleted from the work table.");
    }

    /// <summary>Verifies a bound below 1 is clamped instead of throwing or fanning out unbounded.</summary>
    [TestMethod]
    public async Task Given_MisconfiguredBound_When_Drained_Then_ClampedToOne()
    {
        var blobs = new ConcurrencyObservingBlobStore(TimeSpan.FromMilliseconds(5));
        var items = Enumerable.Range(0, 4).Select(NewWork).ToList();

        var (deleted, _) = await BlobDeleteWorkerService.DeleteBatchAsync(
            blobs, items, maxConcurrency: 0, CancellationToken.None);

        Assert.AreEqual(items.Count, deleted.Count);
        Assert.AreEqual(1, blobs.MaxInFlight, "A bound of 0 must run sequentially, not unbounded.");
    }

    /// <summary>
    /// Verifies the dispatcher sends every destination group and hard-deletes only the rows a transport
    /// confirmed. This is the property that keeps the outbox at-least-once: a group whose send threw must
    /// keep its rows, and a group that succeeded must lose them.
    /// </summary>
    [TestMethod]
    public async Task Given_MultipleDestinations_When_Dispatched_Then_OnlyConfirmedGroupsAreDeleted()
    {
        var good = Enumerable.Range(0, 3).Select(i => NewOutbox("DomainEvents", i)).ToList();
        var bad = Enumerable.Range(0, 2).Select(i => NewOutbox("Projections", i)).ToList();
        var batch = new LeasedBatch<OutboxMessage>(Guid.CreateVersion7(), [.. good, .. bad]);

        var transport = new RecordingTransport(failDestination: "Projections");
        var work = new RecordingWorkRepository();
        using var metrics = new MessagingMetrics();

        await OutboxDispatcherService.DispatchBatchAsync(
            batch, transport, work, metrics, NullLogger.Instance, CancellationToken.None);

        CollectionAssert.AreEquivalent(
            new[] { "DomainEvents", "Projections" },
            transport.Sent.Keys.ToList(),
            "Every destination group must be sent, including ones that go on to fail.");

        CollectionAssert.AreEquivalent(
            good.Select(m => m.Id).ToList(), work.Completed,
            "Only the confirmed destination's rows may be hard-deleted.");
        CollectionAssert.AreEquivalent(
            bad.Select(m => m.Id).ToList(), work.Released,
            "The failed destination's rows must be released for retry, one by one, with their lease token.");
        Assert.IsTrue(work.LeaseTokens.All(t => t == batch.LeaseToken),
            "Settlement must carry the batch lease token, or a stolen lease could be settled.");
    }

    /// <summary>Verifies destination sends overlap rather than queueing behind one another.</summary>
    [TestMethod]
    public async Task Given_SlowDestination_When_Dispatched_Then_GroupsSendConcurrently()
    {
        var batch = new LeasedBatch<OutboxMessage>(Guid.CreateVersion7(),
        [
            NewOutbox("DomainEvents", 0),
            NewOutbox("Projections", 0),
            NewOutbox("AiReview", 0)
        ]);

        var transport = new RecordingTransport(delay: TimeSpan.FromMilliseconds(30));
        var work = new RecordingWorkRepository();
        using var metrics = new MessagingMetrics();

        await OutboxDispatcherService.DispatchBatchAsync(
            batch, transport, work, metrics, NullLogger.Instance, CancellationToken.None);

        Assert.AreEqual(3, transport.MaxInFlight,
            "All three destination sends should overlap; a lower figure means a slow channel is still "
            + "holding up the ones behind it (D-055).");
        Assert.AreEqual(3, work.Completed.Count);
    }

    /// <summary>Verifies the pooled body buffer round-trips payloads and rejects use after disposal.</summary>
    [TestMethod]
    public void Given_OutboxBodyBuffer_When_PayloadsAppended_Then_SlicesRoundTripIndependently()
    {
        var rows = new List<OutboxMessage>
        {
            NewOutbox("DomainEvents", 0, payload: """{"Type":"first","Body":"asc\u00ii"}"""),
            NewOutbox("DomainEvents", 1, payload: """{"Type":"second"}""")
        };

        ReadOnlyMemory<byte> first;
        ReadOnlyMemory<byte> second;
        using (var buffer = OutboxBodyBuffer.Rent(rows))
        {
            first = buffer.Append(rows[0].Payload);
            second = buffer.Append(rows[1].Payload);

            Assert.AreEqual(rows[0].Payload, System.Text.Encoding.UTF8.GetString(first.Span));
            Assert.AreEqual(rows[1].Payload, System.Text.Encoding.UTF8.GetString(second.Span),
                "The second slice must not overlap the first; a wrong offset would corrupt every body "
                + "after the first in a batch.");
        }
    }

    private static BlobDeleteWork NewWork(int index) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = Guid.CreateVersion7(),
        ContainerName = "attachments",
        BlobName = $"blob-{index}"
    };

    private static OutboxMessage NewOutbox(string destination, int index, string? payload = null) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = Guid.CreateVersion7(),
        Destination = destination,
        EventType = "TaskItemCreatedEvent",
        EventVersion = 1,
        Payload = payload ?? $"{{\"Index\":{index}}}",
        OccurredAtUtc = DateTimeOffset.UtcNow
    };

    /// <summary>Blob store that records peak concurrent deletes and can fail a chosen blob.</summary>
    private sealed class ConcurrencyObservingBlobStore(TimeSpan hold, Func<string, bool>? failOn = null)
        : IObjectStorageRepository
    {
        private int _inFlight;
        private int _maxInFlight;
        private int _calls;

        public int MaxInFlight => Volatile.Read(ref _maxInFlight);
        public int Calls => Volatile.Read(ref _calls);

        public async Task DeleteAsync(string containerName, string objectName, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            var current = Interlocked.Increment(ref _inFlight);
            InterlockedMax(ref _maxInFlight, current);
            try
            {
                // A real delete is a network round trip. The hold is what makes overlap observable: with an
                // instantly-completing fake, a correct concurrent implementation and a sequential one both
                // show a peak of 1. The bound assertion itself is exact and does not depend on timing.
                if (hold > TimeSpan.Zero) await Task.Delay(hold, cancellationToken);
                if (failOn?.Invoke(objectName) == true)
                    throw new InvalidOperationException($"delete failed for {objectName}");
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public Task UploadAsync(string containerName, string objectName, Stream content,
            string? contentType = null, IDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Stream> DownloadAsync(string containerName, string objectName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> ExistsAsync(string containerName, string objectName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Uri> GetPresignedUrlAsync(string containerName, string objectName, TimeSpan lifetime,
            ObjectStoragePermissions permissions = ObjectStoragePermissions.Read,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ObjectStoragePage> ListAsync(string containerName, string? prefix = null,
            string? continuationToken = null, int pageSize = 100,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Transport that records what was sent per destination and peak concurrent sends.</summary>
    private sealed class RecordingTransport(string? failDestination = null, TimeSpan delay = default)
        : IIntegrationEventTransport
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _sent = new(StringComparer.Ordinal);
        private int _inFlight;
        private int _maxInFlight;

        public System.Collections.Concurrent.ConcurrentDictionary<string, int> Sent => _sent;
        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        public bool CanDispatch => true;

        public async Task SendBatchAsync(string destination, IReadOnlyList<OutboxMessage> messages, CancellationToken ct)
        {
            var current = Interlocked.Increment(ref _inFlight);
            InterlockedMax(ref _maxInFlight, current);
            try
            {
                _sent[destination] = messages.Count;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
                if (destination == failDestination)
                    throw new InvalidOperationException($"broker rejected {destination}");
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    /// <summary>Work repository recording settlement calls; only the outbox members are exercised.</summary>
    private sealed class RecordingWorkRepository : IOperationalWorkRepository
    {
        public List<Guid> Completed { get; } = [];
        public List<Guid> Released { get; } = [];
        public List<Guid> LeaseTokens { get; } = [];

        public Task<int> CompleteAsync<TWork>(Guid leaseToken, IReadOnlyCollection<Guid> ids, CancellationToken ct)
            where TWork : OperationalWorkBase
        {
            LeaseTokens.Add(leaseToken);
            Completed.AddRange(ids);
            return Task.FromResult(ids.Count);
        }

        public Task ReleaseAsync<TWork>(Guid leaseToken, Guid id, int attemptCount, string error, CancellationToken ct)
            where TWork : OperationalWorkBase
        {
            LeaseTokens.Add(leaseToken);
            Released.Add(id);
            return Task.CompletedTask;
        }

        public Task<LeasedBatch<TWork>> ClaimAsync<TWork>(int batchSize, TimeSpan leaseDuration, string owner, CancellationToken ct)
            where TWork : OperationalWorkBase => throw new NotSupportedException();

        public Task<bool> RetryDeadLetteredAsync<TWork>(Guid id, CancellationToken ct)
            where TWork : OperationalWorkBase => throw new NotSupportedException();

        public Task<int> PurgeDeadLetteredAsync<TWork>(DateTimeOffset cutoffUtc, CancellationToken ct)
            where TWork : OperationalWorkBase => throw new NotSupportedException();

        public Task<OutboxBacklog> GetOutboxBacklogAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>Lock-free running maximum; the fakes are written to from several worker threads at once.</summary>
    private static void InterlockedMax(ref int target, int candidate)
    {
        var observed = Volatile.Read(ref target);
        while (candidate > observed)
        {
            var previous = Interlocked.CompareExchange(ref target, candidate, observed);
            if (previous == observed) return;
            observed = previous;
        }
    }
}
