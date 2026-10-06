extern alias SchedulerHost;

using System.Collections.Concurrent;
using EF.Common.Contracts;
using EF.Data.Contracts;
using EF.Data.Outbox;
using EF.Storage.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SchedulerHost::TaskFlow.Scheduler.Workers;
using TaskFlow.Application.Contracts.Storage;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Cqrs.Features.Attachments;
using TaskFlow.Application.Models;
using TaskFlow.Application.Services;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Domain.Shared.Enums;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Data.Operational;
using TaskFlow.Infrastructure.Repositories;
using Test.Integration.Infrastructure;
using static Test.Integration.Infrastructure.RaceHarness;

namespace Test.Integration;

/// <summary>
/// D-075: an upload saves a blob-delete reservation for its new key before it writes the blob, and the save that inserts
/// the attachment row removes it. Here that save fails after the blob is written, because a concurrent upload with the
/// same caller id wins the insert (D-033): the caller gets the winner as a replay, and the reservation is all that still
/// knows the orphaned blob. The worker's claim leaves it alone during the grace period and, once the grace has passed,
/// one drain deletes the blob and settles the row. Service and CQRS styles, on the lane's relational provider.
/// Component tier: an isolated database on the standalone SQL Testcontainer, so the drain claims only this test's rows.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class AttachmentUploadReservationTests
{
    private const string FileName = "evidence.txt";
    private const string ContentType = "text/plain";
    private static readonly byte[] Content = "evidence"u8.ToArray();
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(15);

    public TestContext TestContext { get; set; } = null!;

    /// <summary>Inconclusive without a container runtime; fails when the SQL container failed to start.</summary>
    [TestInitialize]
    public void TestSetup() => IntegrationTestSetup.AssertAvailable("SQL", DbContainerFixture.StartupError);

    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    [DataRow("Service")]
    [DataRow("Cqrs")]
    public async Task Given_UploadSaveFailsAfterTheBlobIsWritten_When_TheReservationDrains_Then_NoBlobRemains(string style)
    {
        var ct = TestContext.CancellationToken;
        var connStr = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync("uploadreserve", ct);
        await using (var migrate = DbContainerFixture.CreateTrxnContext(connStr))
            await migrate.Database.MigrateAsync(ct);
        var callerId = Guid.CreateVersion7();
        var ownerId = Guid.CreateVersion7();
        // The concurrent winner lands while this upload writes its blob, after its existence check found nothing.
        var blobs = new RacingBlobStorage(async token =>
        {
            await using var other = DbContainerFixture.CreateTrxnContext(connStr);
            other.Attachments.Add(Attachment.Create(TenantId.From(TenantGuid), FileName, ContentType, Content.Length,
                "https://storage.example.com/winner", AttachmentOwnerType.TaskItem, ownerId, AttachmentId.From(callerId), "winner-key").Value!);
            await other.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, cancellationToken: token);
        });

        var result = await UploadAsync(style, connStr, blobs, ownerId, callerId, ct);

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        Assert.IsTrue(result.Value!.IsReplay, "the upload that lost the insert replays the winner");
        var orphan = blobs.Names.Single();
        await using (var verify = DbContainerFixture.CreateTrxnContext(connStr))
        {
            var reservation = await verify.BlobDeleteWork.AsNoTracking().SingleAsync(ct);
            Assert.AreEqual(orphan, reservation.BlobName, "the reservation names the blob the failed upload wrote");
            Assert.IsGreaterThan(DateTimeOffset.UtcNow + Grace - TimeSpan.FromMinutes(1), reservation.AvailableAtUtc);
        }

        var clock = new MutableClock(DateTimeOffset.UtcNow);
        Assert.AreEqual(0, await DrainOnceAsync(connStr, blobs, clock, ct), "the worker cannot claim a reservation inside its grace period");
        Assert.IsTrue(blobs.Names.Contains(orphan));

        clock.Advance(Grace + TimeSpan.FromMinutes(1));
        Assert.AreEqual(1, await DrainOnceAsync(connStr, blobs, clock, ct));

        Assert.IsEmpty(blobs.Names, "the drained reservation deleted the orphaned blob");
        await using (var verify = DbContainerFixture.CreateTrxnContext(connStr))
        {
            Assert.IsFalse(await verify.BlobDeleteWork.AnyAsync(ct), "the drained reservation is settled");
            Assert.AreEqual("winner-key", (await verify.Attachments.IgnoreQueryFilters().SingleAsync(ct)).StorageKey);
        }
    }

    /// <summary>
    /// The blob-delete worker claims the reservation while the upload is still in flight (an upload that outlived the
    /// grace period). The insert's lease-guarded release removes no row, so the transaction rolls back: the upload answers
    /// the save-failed result (400, never 412 on a POST), no attachment row is stored, and when the worker completes its
    /// claim the blob is gone.
    /// </summary>
    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    [DataRow("Service")]
    [DataRow("Cqrs")]
    public async Task Given_ReservationClaimedDuringUpload_When_RowInserted_Then_UploadFailsAndTheWorkerDeletesTheBlob(string style)
    {
        var ct = TestContext.CancellationToken;
        var connStr = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync("uploadclaimed", ct);
        await using (var migrate = DbContainerFixture.CreateTrxnContext(connStr))
            await migrate.Database.MigrateAsync(ct);
        var workerClock = new MutableClock(DateTimeOffset.UtcNow + Grace + TimeSpan.FromMinutes(1));
        LeasedBatch<BlobDeleteWork>? claimed = null;
        var blobs = new RacingBlobStorage(async token =>
        {
            await using var worker = DbContainerFixture.CreateTrxnContext(connStr);
            claimed = await new LeasedWorkStore<TaskFlowDbContextTrxn>(worker, workerClock)
                .ClaimAsync<BlobDeleteWork>(new LeaseRequest(50, TimeSpan.FromMinutes(5), 10, "late-worker"), token);
        });

        var result = await UploadAsync(style, connStr, blobs, Guid.CreateVersion7(), Guid.CreateVersion7(), ct);

        Assert.IsTrue(result.IsFailure, "the upload whose reservation the worker holds is not stored");
        Assert.AreEqual(ErrorConstants.ERROR_SAVE_FAILED, result.ErrorMessage);
        Assert.HasCount(1, claimed!.Items, "the worker held the reservation when the row was inserted");
        await using (var verify = DbContainerFixture.CreateTrxnContext(connStr))
        {
            Assert.IsFalse(await verify.Attachments.IgnoreQueryFilters().AnyAsync(ct), "the rolled-back transaction stored no row");
            Assert.AreEqual(1, await verify.BlobDeleteWork.CountAsync(ct), "the claimed reservation is still the worker's");
        }

        var completed = new ConcurrentBag<Guid>();
        await BlobDeleteWorkerService.DeleteBatchAsync(blobs, claimed.Items, completed.Add,
            (id, ex) => Assert.Fail($"blob delete {id} failed: {ex}"), maxConcurrency: 1, NullLogger.Instance, ct);
        await using (var worker = DbContainerFixture.CreateTrxnContext(connStr))
            Assert.AreEqual(1, await new LeasedWorkStore<TaskFlowDbContextTrxn>(worker, workerClock)
                .CompleteAsync<BlobDeleteWork>(claimed.LeaseToken, [.. completed], ct));

        Assert.IsEmpty(blobs.Names, "the worker deleted the blob of the upload that was not stored");
    }

    /// <summary>Uploads through the style under test.</summary>
    private static async Task<Result<DefaultResponse<AttachmentDto>>> UploadAsync(
        string style, string connStr, IObjectStorageRepository blobs, Guid ownerId, Guid callerId, CancellationToken ct)
    {
        await using var db = DbContainerFixture.CreateTrxnContext(connStr);
        await using var queryDb = DbContainerFixture.CreateQueryContext(connStr);
        using var stream = new MemoryStream(Content);
        var repo = new AttachmentRepositoryTrxn(db, Options.Create(new AttachmentUploadSettings()));
        return style == "Service"
            ? await new AttachmentService(NullLogger<AttachmentService>.Instance, RequestContext(), repo,
                new AttachmentRepositoryQuery(queryDb), Boundary, blobs)
                .UploadAsync(stream, FileName, ContentType, Content.Length, AttachmentOwnerType.TaskItem, ownerId, callerId, ct)
            : await new UploadAttachmentHandler(NullLogger<UploadAttachmentHandler>.Instance, RequestContext(), repo,
                new AttachmentRepositoryQuery(queryDb), Boundary, blobs)
                .HandleAsync(new UploadAttachmentCommand(stream, FileName, ContentType, Content.Length, AttachmentOwnerType.TaskItem, ownerId, callerId), ct);
    }

    /// <summary>
    /// One pass of <see cref="BlobDeleteWorkerService"/>'s drain at <paramref name="clock"/>: the package claim, the
    /// worker's delete step, then the settlement of the completed rows. Returns how many rows the claim took.
    /// </summary>
    private static async Task<int> DrainOnceAsync(string connStr, IObjectStorageRepository blobs, TimeProvider clock, CancellationToken ct)
    {
        await using var db = DbContainerFixture.CreateTrxnContext(connStr);
        var store = new LeasedWorkStore<TaskFlowDbContextTrxn>(db, clock);
        var batch = await store.ClaimAsync<BlobDeleteWork>(new LeaseRequest(50, TimeSpan.FromMinutes(5), 10, "reservation-test"), ct);
        var completed = new ConcurrentBag<Guid>();

        await BlobDeleteWorkerService.DeleteBatchAsync(blobs, batch.Items, completed.Add,
            (id, ex) => Assert.Fail($"blob delete {id} failed: {ex}"), maxConcurrency: 1, NullLogger.Instance, ct);

        if (!completed.IsEmpty)
            Assert.AreEqual(completed.Count, await store.CompleteAsync<BlobDeleteWork>(batch.LeaseToken, [.. completed], ct));
        return batch.Items.Count;
    }

    /// <summary>Clock a test moves forward past the grace period.</summary>
    private sealed class MutableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>In-memory attachment container that runs <paramref name="afterFirstUpload"/> once a blob is stored.</summary>
    private sealed class RacingBlobStorage(Func<CancellationToken, Task> afterFirstUpload) : IObjectStorageRepository
    {
        private readonly ConcurrentDictionary<string, byte[]> _blobs = new();
        private bool _raced;

        public IReadOnlyCollection<string> Names => [.. _blobs.Keys];

        public async Task UploadAsync(string containerName, string objectName, Stream content, string? contentType = null,
            IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
        {
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, cancellationToken);
            _blobs[objectName] = copy.ToArray();
            if (_raced) return;
            _raced = true;
            await afterFirstUpload(cancellationToken);
        }

        public Task DeleteAsync(string containerName, string objectName, CancellationToken cancellationToken = default)
        {
            _blobs.TryRemove(objectName, out _);
            return Task.CompletedTask;
        }

        public Task<Uri> GetPresignedUrlAsync(string containerName, string objectName, TimeSpan lifetime,
            ObjectStoragePermissions permissions = ObjectStoragePermissions.Read, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Uri($"https://inmemory.blob.local/{containerName}/{objectName}"));

        public Task<Stream> DownloadAsync(string containerName, string objectName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<bool> ExistsAsync(string containerName, string objectName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ObjectStoragePage> ListAsync(string containerName, string? prefix = null, string? continuationToken = null,
            int pageSize = 100, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
