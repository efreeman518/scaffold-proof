using System.Data.Common;
using EF.Cache;
using EF.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TaskFlow.Application.Contracts.Caching;
using TaskFlow.Application.Contracts.Storage;
using TaskFlow.Application.Cqrs.Features.Attachments;
using TaskFlow.Application.Cqrs.Features.Categories;
using TaskFlow.Application.Services;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Infrastructure.Data.Provider;
using TaskFlow.Infrastructure.Repositories;
using Test.Integration.Infrastructure;
using Test.Support.Builders;
using static Test.Integration.Infrastructure.RaceHarness;

namespace Test.Integration;

/// <summary>
/// D-073: an <c>If-Match: *</c> delete whose save landed but was reported failed. The provider's retrying strategy
/// re-sends the save, which then deletes nothing and fails as a lost save; the fresh-read retry re-reads, finds the row
/// gone, and must still run the after-save effect the first attempt's delete earned: the Category cache eviction. The
/// Attachment blob delete is a work row committed with the row (D-026), so the landed commit queues it exactly once. A
/// first attempt that finds no row has no effect. Service and CQRS styles, on the lane's relational provider (PostgreSQL
/// on NonAzure, SQL Server on Azure).
/// Component tier: standalone SQL Testcontainer via <c>DbContainerFixture</c>.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class WildcardDeleteLandedCommitTests
{
    private const string Service = "Service";
    private const string Cqrs = "Cqrs";

    public TestContext TestContext { get; set; } = null!;

    /// <summary>Ensures the shared SQL schema exists before this class runs (idempotent migrate).</summary>
    [ClassInitialize]
    public static async Task ClassInit(TestContext context)
    {
        if (IntegrationTestSetup.IsUnavailable(DbContainerFixture.StartupError))
            return;
        await using var db = DbContainerFixture.CreateTrxnContext();
        await db.Database.MigrateAsync(context.CancellationToken);
    }

    /// <summary>Inconclusive without a container runtime; fails when the SQL container failed to start.</summary>
    [TestInitialize]
    public void TestSetup() => IntegrationTestSetup.AssertAvailable("SQL", DbContainerFixture.StartupError);

    /// <summary>The category is deleted by the first attempt, and the retry that finds it gone still evicts the cache.</summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    [DataRow(Service)]
    [DataRow(Cqrs)]
    public async Task Given_CategoryDeleteCommitLandsButFails_When_DeletedWithWildcard_Then_TheCacheIsEvicted(string style)
    {
        var ct = TestContext.CancellationToken;
        var categoryId = await SeedCategoryAsync(ct);
        var (cache, cached) = await PrimeCategoryCacheAsync(ct);
        var fault = new LandedSaveFault();

        var result = await DeleteCategoryAsync(style, categoryId, cache, fault, ct);

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        Assert.AreEqual(1, fault.Faults, "the save landed and was then reported failed once");
        await using (var verify = DbContainerFixture.CreateTrxnContext())
            Assert.IsFalse(await verify.Categories.IgnoreQueryFilters().AnyAsync(c => c.Id == CategoryId.From(categoryId), ct));
        Assert.IsTrue(await IsEvictedAsync(cache, cached, ct), "the delete the first attempt made must evict the category cache");
    }

    /// <summary>A wildcard delete whose first read finds no category deletes nothing, so it evicts nothing.</summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    [DataRow(Service)]
    [DataRow(Cqrs)]
    public async Task Given_NoCategory_When_DeletedWithWildcard_Then_TheCacheIsKept(string style)
    {
        var ct = TestContext.CancellationToken;
        var (cache, cached) = await PrimeCategoryCacheAsync(ct);

        var result = await DeleteCategoryAsync(style, Guid.CreateVersion7(), cache, new LandedSaveFault(), ct);

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        Assert.IsFalse(await IsEvictedAsync(cache, cached, ct), "a first attempt that finds no row has no after-save effect");
    }

    /// <summary>
    /// The attachment row is deleted by the first attempt, whose landed commit staged the blob delete with it; the retry
    /// that finds the row gone succeeds and stages nothing more.
    /// </summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    [DataRow(Service)]
    [DataRow(Cqrs)]
    public async Task Given_AttachmentDeleteCommitLandsButFails_When_DeletedWithWildcard_Then_OneBlobDeleteIsStaged(string style)
    {
        var ct = TestContext.CancellationToken;
        var fileName = $"landed-{Guid.NewGuid():N}.pdf";
        var attachment = new AttachmentBuilder().WithTenantId(TenantGuid).WithFileName(fileName)
            .WithStorageKey(AttachmentBlobs.NewObjectKey(TenantGuid, Guid.NewGuid(), fileName)).Build();
        await using (var seed = DbContainerFixture.CreateTrxnContext())
        {
            seed.Attachments.Add(attachment);
            await seed.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, cancellationToken: ct);
        }

        var fault = new LandedSaveFault();
        var result = await DeleteAttachmentAsync(style, attachment.Id.Value, fault, ct);
        var none = await DeleteAttachmentAsync(style, Guid.CreateVersion7(), new LandedSaveFault(), ct);

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        Assert.IsTrue(none.IsSuccess, none.ErrorMessage);
        Assert.AreEqual(1, fault.Faults, "the save landed and was then reported failed once");
        await using var verify = DbContainerFixture.CreateTrxnContext();
        Assert.IsFalse(await verify.Attachments.IgnoreQueryFilters().AnyAsync(a => a.Id == attachment.Id, ct));
        CollectionAssert.AreEqual(
            new[] { $"{AttachmentBlobs.ContainerName}/{attachment.StorageKey}" },
            await verify.BlobDeleteWork.Where(w => w.BlobName == attachment.StorageKey)
                .Select(w => w.ContainerName + "/" + w.BlobName).ToListAsync(ct),
            "the landed delete staged its blob once; the absent row staged none");
    }

    private static async Task<EF.Common.Contracts.Result> DeleteCategoryAsync(
        string style, Guid categoryId, ITypedCache cache, LandedSaveFault fault, CancellationToken ct)
    {
        var connStr = DbContainerFixture.ConnectionString;
        await using var db = DbContainerFixture.CreateTrxnContext(connStr, fault);
        await using var queryDb = DbContainerFixture.CreateQueryContext(connStr);
        var repo = new CategoryRepositoryTrxn(db);
        return style == Service
            ? await new CategoryService(NullLogger<CategoryService>.Instance, RequestContext(), repo,
                new CategoryRepositoryQuery(queryDb), Boundary, cache).DeleteAsync(categoryId, expectedVersion: null, ct)
            : await new DeleteCategoryHandler(NullLogger<DeleteCategoryHandler>.Instance, RequestContext(), repo, Boundary, cache)
                .HandleAsync(new DeleteCategoryCommand(categoryId, ExpectedVersion: null), ct);
    }

    private static async Task<EF.Common.Contracts.Result> DeleteAttachmentAsync(
        string style, Guid attachmentId, LandedSaveFault fault, CancellationToken ct)
    {
        var connStr = DbContainerFixture.ConnectionString;
        await using var db = DbContainerFixture.CreateTrxnContext(connStr, fault);
        await using var queryDb = DbContainerFixture.CreateQueryContext(connStr);
        var repo = new AttachmentRepositoryTrxn(db);
        return style == Service
            ? await new AttachmentService(NullLogger<AttachmentService>.Instance, RequestContext(), repo,
                new AttachmentRepositoryQuery(queryDb), Boundary).DeleteAsync(attachmentId, expectedVersion: null, ct)
            : await new DeleteAttachmentHandler(NullLogger<DeleteAttachmentHandler>.Instance, RequestContext(), repo, Boundary, NewCache())
                .HandleAsync(new DeleteAttachmentCommand(attachmentId, ExpectedVersion: null), ct);
    }

    private static async Task<Guid> SeedCategoryAsync(CancellationToken ct)
    {
        await using var db = DbContainerFixture.CreateTrxnContext();
        var category = new CategoryBuilder().WithTenantId(TenantGuid).WithName($"Landed-{Guid.NewGuid():N}"[..20]).Build();
        db.Categories.Add(category);
        await db.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, cancellationToken: ct);
        return category.Id.Value;
    }

    /// <summary>A private cache holding one entry that depends on the tenant's categories, as both styles evict by.</summary>
    private static async Task<(ITypedCache Cache, CacheKey Key)> PrimeCategoryCacheAsync(CancellationToken ct)
    {
        var cache = NewCache();
        var key = TaskFlowCache.Key(CacheKind.TaskMetadata, Guid.NewGuid());
        await cache.GetOrSetAsync(key, _ => Task.FromResult("cached"), CacheProfiles.Metadata,
            [CacheTags.Entity(TenantGuid, CacheTags.Category)], ct);
        return (cache, key);
    }

    private static async Task<bool> IsEvictedAsync(ITypedCache cache, CacheKey key, CancellationToken ct) =>
        await cache.GetOrSetAsync(key, _ => Task.FromResult("recomputed"), CacheProfiles.Metadata,
            [CacheTags.Entity(TenantGuid, CacheTags.Category)], ct) == "recomputed";

    /// <summary>
    /// Once, right after the delete's write is durable, throws an exception the lane's retrying strategy retries: at the
    /// commit when the save runs in a transaction, or after the DELETE command when it autocommits. The strategy then
    /// re-sends the save against a row that is gone. A PostgresException 40001 for Npgsql; a TimeoutException for SQL
    /// Server (retried by <c>SqlServerTransientExceptionDetector</c>, constructible without reflection).
    /// </summary>
    private sealed class LandedSaveFault : DbTransactionInterceptor, IDbCommandInterceptor
    {
        private bool _deleteSent;
        public int Faults { get; private set; }

        public ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            OnCommandExecuted(command, eventData);
            return ValueTask.FromResult(result);
        }

        public ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            OnCommandExecuted(command, eventData);
            return ValueTask.FromResult(result);
        }

        public override Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (_deleteSent) ThrowOnce();
            return Task.CompletedTask;
        }

        private void OnCommandExecuted(DbCommand command, CommandExecutedEventData eventData)
        {
            if (!command.CommandText.Contains("DELETE FROM", StringComparison.OrdinalIgnoreCase)) return;
            _deleteSent = true;
            if (eventData.Context?.Database.CurrentTransaction is null) ThrowOnce();
        }

        private void ThrowOnce()
        {
            if (Faults > 0) return;
            Faults++;
            throw DbContainerFixture.Provider == TaskFlowDbProvider.PostgreSql
                ? new PostgresException("injected serialization failure", "ERROR", "ERROR", PostgresErrorCodes.SerializationFailure)
                : new TimeoutException("injected commit timeout");
        }
    }
}
