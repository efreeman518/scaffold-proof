using EF.Storage.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using TaskFlow.Application.Contracts.Storage;
using System.Collections.Concurrent;
using TaskFlow.Infrastructure.Data.Operational;
using TaskFlow.Scheduler.Workers;

namespace Test.Unit.Hosting;

/// <summary>
/// Covers the D-055 bounded-concurrency blob-delete drain. The bound is the whole point of the change: an
/// unbounded fan-out over a claimed batch of 50 would hammer the storage account, and a bound that silently
/// degraded back to 1 would look identical in every other test. Asserted here with a fake that observes
/// concurrency directly. (The outbox dispatcher's concurrent destination sends are EF.Data.Outbox's, covered by
/// its own tests.)
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

        var result = new Outcomes();
        await BlobDeleteWorkerService.DeleteBatchAsync(
            blobs, items, result.Complete, result.Fail, maxConcurrency, NullLogger.Instance, CancellationToken.None);

        Assert.IsTrue(items.All(i => result.Completed.Contains(i.Id)), "Every row should be reported as deleted.");
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

        var result = new Outcomes();
        await BlobDeleteWorkerService.DeleteBatchAsync(
            blobs, items, result.Complete, result.Fail, maxConcurrency: 4, NullLogger.Instance, CancellationToken.None);

        Assert.AreEqual(5, items.Count(i => result.Completed.Contains(i.Id)));
        Assert.IsFalse(result.Completed.Contains(doomed.Id), "A row whose delete threw must not be hard-deleted from the work table.");
        Assert.IsTrue(result.Failed.TryGetValue(doomed.Id, out var error), "A failed delete is reported so it is retried with backoff.");
        Assert.IsInstanceOfType<InvalidOperationException>(error);
    }

    /// <summary>Verifies a bound below 1 is clamped instead of throwing or fanning out unbounded.</summary>
    [TestMethod]
    public async Task Given_MisconfiguredBound_When_Drained_Then_ClampedToOne()
    {
        var blobs = new ConcurrencyObservingBlobStore(TimeSpan.FromMilliseconds(5));
        var items = Enumerable.Range(0, 4).Select(NewWork).ToList();

        var result = new Outcomes();
        await BlobDeleteWorkerService.DeleteBatchAsync(
            blobs, items, result.Complete, result.Fail, maxConcurrency: 0, NullLogger.Instance, CancellationToken.None);

        Assert.IsTrue(items.All(i => result.Completed.Contains(i.Id)));
        Assert.AreEqual(1, blobs.MaxInFlight, "A bound of 0 must run sequentially, not unbounded.");
    }

    private static BlobDeleteWork NewWork(int index) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = Guid.CreateVersion7(),
        ContainerName = "attachments",
        BlobName = $"blob-{index}"
    };

    /// <summary>What the drain reported; stands in for the worker's thread-safe batch result.</summary>
    private sealed class Outcomes
    {
        public ConcurrentBag<Guid> Completed { get; } = [];
        public ConcurrentDictionary<Guid, Exception> Failed { get; } = new();

        public void Complete(Guid id) => Completed.Add(id);

        public void Fail(Guid id, Exception error) => Failed[id] = error;
    }

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
