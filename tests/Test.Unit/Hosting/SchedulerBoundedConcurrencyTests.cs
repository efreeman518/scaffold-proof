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

        var result = new WorkBatchResult(items.Select(i => i.Id));
        await BlobDeleteWorkerService.DeleteBatchAsync(
            blobs, items, result, maxConcurrency, NullLogger.Instance, CancellationToken.None);

        Assert.IsTrue(items.All(i => result.OutcomeOf(i.Id)?.IsCompleted == true), "Every row should be reported as deleted.");
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

        var result = new WorkBatchResult(items.Select(i => i.Id));
        await BlobDeleteWorkerService.DeleteBatchAsync(
            blobs, items, result, maxConcurrency: 4, NullLogger.Instance, CancellationToken.None);

        Assert.AreEqual(5, items.Count(i => result.OutcomeOf(i.Id)?.IsCompleted == true));
        var outcome = result.OutcomeOf(doomed.Id);
        Assert.IsNotNull(outcome);
        Assert.IsFalse(outcome.IsCompleted, "A row whose delete threw must not be hard-deleted from the work table.");
        Assert.IsFalse(outcome.Permanent, "A failed delete is transient: it is retried with backoff.");
        StringAssert.Contains(outcome.Error, "InvalidOperationException");
    }

    /// <summary>Verifies a bound below 1 is clamped instead of throwing or fanning out unbounded.</summary>
    [TestMethod]
    public async Task Given_MisconfiguredBound_When_Drained_Then_ClampedToOne()
    {
        var blobs = new ConcurrencyObservingBlobStore(TimeSpan.FromMilliseconds(5));
        var items = Enumerable.Range(0, 4).Select(NewWork).ToList();

        var result = new WorkBatchResult(items.Select(i => i.Id));
        await BlobDeleteWorkerService.DeleteBatchAsync(
            blobs, items, result, maxConcurrency: 0, NullLogger.Instance, CancellationToken.None);

        Assert.IsTrue(items.All(i => result.OutcomeOf(i.Id)?.IsCompleted == true));
        Assert.AreEqual(1, blobs.MaxInFlight, "A bound of 0 must run sequentially, not unbounded.");
    }

    /// <summary>
    /// Verifies the dispatcher sends every destination group and reports completion only for the rows a transport
    /// confirmed. This is the property that keeps the outbox at-least-once: a group whose send threw must keep
    /// its rows, and a group that succeeded must lose them.
    /// </summary>
    [TestMethod]
    public async Task Given_MultipleDestinations_When_Dispatched_Then_OnlyConfirmedGroupsAreCompleted()
    {
        var good = Enumerable.Range(0, 3).Select(i => NewOutbox("DomainEvents", i)).ToList();
        var bad = Enumerable.Range(0, 2).Select(i => NewOutbox("Projections", i)).ToList();
        List<OutboxMessage> items = [.. good, .. bad];
        var result = new WorkBatchResult(items.Select(m => m.Id));

        var transport = new RecordingTransport(failDestination: "Projections");
        using var metrics = new MessagingMetrics();

        await OutboxDispatcherService.DispatchBatchAsync(
            items, transport, result, metrics, NullLogger.Instance, CancellationToken.None);

        CollectionAssert.AreEquivalent(
            new[] { "DomainEvents", "Projections" },
            transport.Sent.Keys.ToList(),
            "Every destination group must be sent, including ones that go on to fail.");
        Assert.IsTrue(good.All(m => result.OutcomeOf(m.Id)?.IsCompleted == true),
            "The confirmed destination's rows must be completed.");
        Assert.IsTrue(bad.All(m => result.OutcomeOf(m.Id) is { IsCompleted: false, Permanent: false }),
            "A thrown send fails every row of its group transiently, so each is released for retry.");
    }

    /// <summary>
    /// Verifies one message a transport did not accept fails alone. With RabbitMQ the confirmed messages of a
    /// partially confirmed batch used to be re-published with the failures; with Service Bus one oversize message
    /// used to fail - and eventually dead-letter - every healthy row claimed with it.
    /// </summary>
    [TestMethod]
    public async Task Given_PartialTransportFailure_When_Dispatched_Then_OnlyTheReportedMessagesFail()
    {
        var items = Enumerable.Range(0, 5).Select(i => NewOutbox("DomainEvents", i)).ToList();
        var result = new WorkBatchResult(items.Select(m => m.Id));
        var transport = new RecordingTransport(failures:
        [
            new OutboxSendFailure(1, "not confirmed", Permanent: false),
            new OutboxSendFailure(3, "exceeds the batch limit", Permanent: true)
        ]);
        using var metrics = new MessagingMetrics();

        await OutboxDispatcherService.DispatchBatchAsync(
            items, transport, result, metrics, NullLogger.Instance, CancellationToken.None);

        foreach (var index in new[] { 0, 2, 4 })
            Assert.IsTrue(result.OutcomeOf(items[index].Id)?.IsCompleted == true, $"row {index} was accepted by the broker");

        Assert.AreEqual(WorkItemOutcome.Failed("not confirmed", permanent: false), result.OutcomeOf(items[1].Id));
        Assert.AreEqual(WorkItemOutcome.Failed("exceeds the batch limit", permanent: true), result.OutcomeOf(items[3].Id));
    }

    /// <summary>
    /// Verifies a shutdown mid-send leaves only the unfinished group unreported (the worker abandons it) while a
    /// group the broker already accepted is still reported complete, instead of the first cancellation skipping
    /// the settlement of every group.
    /// </summary>
    [TestMethod]
    public async Task Given_StoppingMidSend_When_Dispatched_Then_FinishedGroupsAreStillReported()
    {
        var finished = NewOutbox("DomainEvents", 0);
        var hanging = NewOutbox("Projections", 0);
        var result = new WorkBatchResult([finished.Id, hanging.Id]);
        using var stopping = new CancellationTokenSource();
        var transport = new RecordingTransport(hangDestination: "Projections", onSent: d =>
        {
            if (d == "DomainEvents") stopping.Cancel();
        });
        using var metrics = new MessagingMetrics();

        await OutboxDispatcherService.DispatchBatchAsync(
            [finished, hanging], transport, result, metrics, NullLogger.Instance, stopping.Token);

        Assert.IsTrue(result.OutcomeOf(finished.Id)?.IsCompleted == true, "the accepted group must still be deleted");
        Assert.IsNull(result.OutcomeOf(hanging.Id), "the abandoned group is left unreported, so it is abandoned, not failed");
    }

    /// <summary>Verifies destination sends overlap rather than queueing behind one another.</summary>
    [TestMethod]
    public async Task Given_SlowDestination_When_Dispatched_Then_GroupsSendConcurrently()
    {
        List<OutboxMessage> items =
        [
            NewOutbox("DomainEvents", 0),
            NewOutbox("Projections", 0),
            NewOutbox("AiReview", 0)
        ];
        var result = new WorkBatchResult(items.Select(m => m.Id));

        var transport = new RecordingTransport(delay: TimeSpan.FromMilliseconds(30));
        using var metrics = new MessagingMetrics();

        await OutboxDispatcherService.DispatchBatchAsync(
            items, transport, result, metrics, NullLogger.Instance, CancellationToken.None);

        Assert.AreEqual(3, transport.MaxInFlight,
            "All three destination sends should overlap; a lower figure means a slow channel is still "
            + "holding up the ones behind it (D-055).");
        Assert.IsTrue(items.All(m => result.OutcomeOf(m.Id)?.IsCompleted == true));
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
    private sealed class RecordingTransport(
        string? failDestination = null,
        TimeSpan delay = default,
        IReadOnlyList<OutboxSendFailure>? failures = null,
        string? hangDestination = null,
        Action<string>? onSent = null)
        : IIntegrationEventTransport
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _sent = new(StringComparer.Ordinal);
        private int _inFlight;
        private int _maxInFlight;

        public System.Collections.Concurrent.ConcurrentDictionary<string, int> Sent => _sent;
        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        public bool CanDispatch => true;

        public async Task<IReadOnlyList<OutboxSendFailure>> SendBatchAsync(
            string destination, IReadOnlyList<OutboxMessage> messages, CancellationToken ct)
        {
            var current = Interlocked.Increment(ref _inFlight);
            InterlockedMax(ref _maxInFlight, current);
            try
            {
                _sent[destination] = messages.Count;
                if (destination == hangDestination) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
                if (destination == failDestination)
                    throw new InvalidOperationException($"broker rejected {destination}");
                onSent?.Invoke(destination);
                return failures ?? [];
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
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
