using EF.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Domain.Shared;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Data.Operational;
using TaskFlow.Infrastructure.Repositories;
using Test.Integration.Infrastructure;
using Test.Support;

namespace Test.Integration;

/// <summary>
/// D-074 on the lane's relational provider (PostgreSQL on NonAzure, SQL Server on Azure): the unique
/// (TenantId, Scope, Key) index is what turns a concurrent duplicate into one entity, and the in-memory provider
/// used by Test.Endpoints enforces no unique index. The repository cases run against a component database; the
/// HTTP cases boot the real API host against its own migrated database. Inconclusive without a container runtime;
/// fails when the SQL container did not start.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class IdempotencyKeyIntegrationTests
{
    private const string Scope = "task-item.create";
    private const string Header = "Idempotency-Key";
    private static readonly Guid TenantId = TestConstants.TenantId;

    private static string _connectionString = null!;
    private static FlowEngineWorkflowApiFactory? _factory;
    private static readonly CompetingKey Race = new();

    public TestContext TestContext { get; set; } = null!;

    /// <summary>Migrates one isolated database and boots one API host on it whose idempotency-key saves can be raced.</summary>
    [ClassInitialize]
    public static async Task ClassInit(TestContext context)
    {
        if (IntegrationTestSetup.IsUnavailable(DbContainerFixture.StartupError))
            return;
        var ct = context.CancellationToken;
        _connectionString = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync("TaskFlow_IdempotencyKey", ct);
        await using (var trxn = DbContainerFixture.CreateTrxnContext(_connectionString))
            await trxn.Database.MigrateAsync(ct);
        await using (var flowEngine = DbContainerFixture.CreateFlowEngineContext(_connectionString))
            await flowEngine.Database.MigrateAsync(ct);

        _factory = new FlowEngineWorkflowApiFactory(
            _connectionString,
            _ => "{}",
            configureServices: services =>
            {
                // The host's repository, on a context that carries the race interceptor; disarmed it is a no-op.
                services.AddScoped(_ => new RaceContext(DbContainerFixture.CreateTrxnContext(_connectionString, Race)));
                services.AddScoped<IIdempotencyKeyRepository>(sp =>
                    new IdempotencyKeyRepository(sp.GetRequiredService<RaceContext>().Db));
            });
    }

    /// <summary>Disposes the shared API host.</summary>
    [ClassCleanup]
    public static void ClassCleanup() => _factory?.Dispose();

    /// <summary>Inconclusive without a container runtime; fails when the SQL container failed to start.</summary>
    [TestInitialize]
    public void TestSetup() => IntegrationTestSetup.AssertAvailable("SQL", DbContainerFixture.StartupError);

    /// <summary>The same key maps to one stored id; another key, or the same key in another tenant or scope, does not.</summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task GetOrAdd_SameKey_ReturnsTheStoredId_AndOtherKeysTenantsAndScopesGetTheirOwn()
    {
        var ct = TestContext.CancellationToken;
        var key = NewKey();

        var first = await WithRepositoryAsync(r => r.GetOrAddEntityIdAsync(TenantId, Scope, key, ct));
        var again = await WithRepositoryAsync(r => r.GetOrAddEntityIdAsync(TenantId, Scope, key, ct));
        var otherKey = await WithRepositoryAsync(r => r.GetOrAddEntityIdAsync(TenantId, Scope, NewKey(), ct));
        var otherTenant = await WithRepositoryAsync(r => r.GetOrAddEntityIdAsync(Guid.CreateVersion7(), Scope, key, ct));
        var otherScope = await WithRepositoryAsync(r => r.GetOrAddEntityIdAsync(TenantId, "task-item.comment.add:x", key, ct));

        Assert.AreEqual(first, again);
        Assert.AreEqual(7, first.Version, "the mapped id is a UUIDv7 (GR-17)");
        Assert.AreEqual(4, new[] { first, otherKey, otherTenant, otherScope }.Distinct().Count());
        Assert.AreEqual(1, await CountMappingsAsync(TenantId, Scope, key, ct));
    }

    /// <summary>
    /// Keys are compared ordinally on both providers: "abc" and "ABC" map to two ids and store two rows. SQL Server's
    /// default collation is case-insensitive, so without the binary key collation the second lookup found the first
    /// row (and its insert would have failed on the unique index).
    /// </summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task GetOrAdd_KeysThatDifferOnlyByCase_AreDistinctKeys()
    {
        var ct = TestContext.CancellationToken;
        var tenant = Guid.CreateVersion7();

        var lower = await WithRepositoryAsync(r => r.GetOrAddEntityIdAsync(tenant, Scope, "abc", ct));
        var upper = await WithRepositoryAsync(r => r.GetOrAddEntityIdAsync(tenant, Scope, "ABC", ct));
        var scopeUpper = await WithRepositoryAsync(r => r.GetOrAddEntityIdAsync(tenant, Scope.ToUpperInvariant(), "abc", ct));

        Assert.AreNotEqual(lower, upper, "a key that differs only by case is another key");
        Assert.AreNotEqual(lower, scopeUpper, "a scope that differs only by case is another scope");
        Assert.AreEqual(lower, await WithRepositoryAsync(r => r.GetOrAddEntityIdAsync(tenant, Scope, "abc", ct)));
        await using var verify = DbContainerFixture.CreateTrxnContext(_connectionString);
        Assert.AreEqual(3, await verify.IdempotencyKeys.CountAsync(m => m.TenantId == tenant, ct));
    }

    /// <summary>
    /// A concurrent duplicate stores the same key between this request's read and its insert. The insert loses on the
    /// unique index, the existence read finds the winner's row, and this request uses the winner's id.
    /// </summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task GetOrAdd_WhenAConcurrentDuplicateStoresTheKeyFirst_ReturnsTheWinnersId()
    {
        var ct = TestContext.CancellationToken;
        var key = NewKey();
        var winner = Race.Arm(TenantId, Scope, key);

        var id = await WithRepositoryAsync(r => r.GetOrAddEntityIdAsync(TenantId, Scope, key, ct), Race);

        Assert.IsTrue(Race.Ran, "the race must have been staged before the mapping save");
        Assert.AreEqual(winner, id);
        Assert.AreEqual(1, await CountMappingsAsync(TenantId, Scope, key, ct));
    }

    /// <summary>The retention purge deletes mappings created before the cutoff and keeps the rest.</summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task Purge_DeletesMappingsOlderThanTheCutoff()
    {
        var ct = TestContext.CancellationToken;
        var tenant = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        await using (var seed = DbContainerFixture.CreateTrxnContext(_connectionString))
        {
            seed.IdempotencyKeys.AddRange(
                Mapping(tenant, "old", now.AddDays(-8)),
                Mapping(tenant, "recent", now.AddDays(-1)));
            await seed.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, cancellationToken: ct);
        }

        var purged = await WithRepositoryAsync(r => r.PurgeAsync(now.AddDays(-7), ct));

        Assert.IsGreaterThanOrEqualTo(1, purged);
        await using var verify = DbContainerFixture.CreateTrxnContext(_connectionString);
        var left = await verify.IdempotencyKeys.AsNoTracking().Where(m => m.TenantId == tenant).Select(m => m.Key).ToListAsync(ct);
        CollectionAssert.AreEquivalent(new[] { "recent" }, left);
    }

    /// <summary>
    /// The purge deletes in bounded batches (like the inbox purge), so a large sweep is several short statements rather
    /// than one that could escalate to a table lock and block create-path inserts.
    /// </summary>
    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public async Task Purge_DeletesInBatchesOfAtMostTheBatchSize()
    {
        var ct = TestContext.CancellationToken;
        var tenant = Guid.CreateVersion7();
        var old = DateTimeOffset.UtcNow.AddDays(-8);
        await using (var seed = DbContainerFixture.CreateTrxnContext(_connectionString))
        {
            seed.IdempotencyKeys.AddRange(Enumerable.Range(0, (2 * IdempotencyKeyRepository.PurgeBatchSize) + 1)
                .Select(i => Mapping(tenant, $"bulk-{i}", old)));
            await seed.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, cancellationToken: ct);
        }
        var deletes = new DeleteCounter();

        var purged = await WithRepositoryAsync(r => r.PurgeAsync(old.AddDays(1), ct), deletes);

        Assert.IsGreaterThanOrEqualTo((2 * IdempotencyKeyRepository.PurgeBatchSize) + 1, purged);
        Assert.IsGreaterThanOrEqualTo(3, deletes.Statements, "2001 rows in batches of 1000 take at least three statements");
        await using var verify = DbContainerFixture.CreateTrxnContext(_connectionString);
        Assert.AreEqual(0, await verify.IdempotencyKeys.CountAsync(m => m.TenantId == tenant, ct));
    }

    /// <summary>Through the API: the same key twice creates one task row and replays it; another key creates a second.</summary>
    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public async Task Post_SameKeyTwice_CreatesOneTaskRow_AndADifferentKeyCreatesAnother()
    {
        var ct = TestContext.CancellationToken;
        using var client = _factory!.CreateClient();
        var key = NewKey();
        var title = $"Keyed-{Guid.NewGuid():N}";

        var (firstStatus, firstId) = await CreateTaskAsync(client, title, key, ct);
        var (secondStatus, secondId) = await CreateTaskAsync(client, title, key, ct);
        var (otherStatus, otherId) = await CreateTaskAsync(client, title, NewKey(), ct);

        Assert.AreEqual(HttpStatusCode.Created, firstStatus);
        Assert.AreEqual(HttpStatusCode.OK, secondStatus, "the resend replays the stored row");
        Assert.AreEqual(firstId, secondId);
        Assert.AreEqual(HttpStatusCode.Created, otherStatus);
        Assert.AreNotEqual(firstId, otherId);
        Assert.AreEqual(2, await CountTasksAsync(title, ct));
    }

    /// <summary>Through the API: the same key twice adds one comment row.</summary>
    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public async Task Post_SameKeyTwice_AddsOneCommentRow()
    {
        var ct = TestContext.CancellationToken;
        using var client = _factory!.CreateClient();
        var (_, taskId) = await CreateTaskAsync(client, $"Root-{Guid.NewGuid():N}", key: null, ct);
        var key = NewKey();

        using var first = await PostAsync(client, $"/api/v1/task-items/{taskId}/comments", new { item = new { body = "keyed" } }, key, ct);
        using var second = await PostAsync(client, $"/api/v1/task-items/{taskId}/comments", new { item = new { body = "keyed" } }, key, ct);

        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode, await first.Content.ReadAsStringAsync(ct));
        Assert.AreEqual(HttpStatusCode.OK, second.StatusCode, await second.Content.ReadAsStringAsync(ct));
        await using var verify = DbContainerFixture.CreateTrxnContext(_connectionString);
        Assert.AreEqual(1, await verify.Comments.IgnoreQueryFilters().CountAsync(c => c.TaskItemId == TaskItemId.From(taskId), ct));
    }

    /// <summary>
    /// Through the API, two concurrent same-key creates: the competitor's mapping is committed first, so this request
    /// loses its mapping insert and creates with the competitor's id; the competitor's own create then replays it.
    /// One task row results.
    /// </summary>
    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public async Task Post_ConcurrentSameKey_ResultsInOneTask()
    {
        var ct = TestContext.CancellationToken;
        using var client = _factory!.CreateClient();
        var key = NewKey();
        var title = $"Raced-{Guid.NewGuid():N}";
        var winner = Race.Arm(TenantId, Scope, key);

        var (status, id) = await CreateTaskAsync(client, title, key, ct);
        var (competitorStatus, competitorId) = await CreateTaskAsync(client, title, key, ct);

        Assert.IsTrue(Race.Ran, "the race must have been staged inside the host's mapping save");
        Assert.AreEqual(HttpStatusCode.Created, status);
        Assert.AreEqual(winner, id, "the losing insert must reuse the competitor's id");
        Assert.AreEqual(HttpStatusCode.OK, competitorStatus);
        Assert.AreEqual(winner, competitorId);
        Assert.AreEqual(1, await CountTasksAsync(title, ct));
    }

    private static string NewKey() => $"key-{Guid.NewGuid():N}";

    private static IdempotencyKeyRecord Mapping(Guid tenant, string key, DateTimeOffset createdUtc) => new()
    {
        EntityId = Guid.CreateVersion7(createdUtc),
        TenantId = tenant,
        Scope = Scope,
        Key = key,
        CreatedUtc = createdUtc
    };

    private static async Task<T> WithRepositoryAsync<T>(Func<IIdempotencyKeyRepository, Task<T>> action, params IInterceptor[] interceptors)
    {
        await using var db = DbContainerFixture.CreateTrxnContext(_connectionString, interceptors);
        return await action(new IdempotencyKeyRepository(db));
    }

    private static async Task<int> CountMappingsAsync(Guid tenant, string scope, string key, CancellationToken ct)
    {
        await using var db = DbContainerFixture.CreateTrxnContext(_connectionString);
        return await db.IdempotencyKeys.CountAsync(m => m.TenantId == tenant && m.Scope == scope && m.Key == key, ct);
    }

    private static async Task<int> CountTasksAsync(string title, CancellationToken ct)
    {
        await using var db = DbContainerFixture.CreateTrxnContext(_connectionString);
        return await db.TaskItems.IgnoreQueryFilters().CountAsync(t => t.Title == title, ct);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, object body, string? key, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (key is not null) request.Headers.TryAddWithoutValidation(Header, key);
        return await client.SendAsync(request, ct);
    }

    private static async Task<(HttpStatusCode Status, Guid Id)> CreateTaskAsync(HttpClient client, string title, string? key, CancellationToken ct)
    {
        using var response = await PostAsync(client, "/api/v1/task-items", new { item = new { title } }, key, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.IsTrue(response.IsSuccessStatusCode, $"Create failed: {(int)response.StatusCode} {body}");
        using var payload = JsonDocument.Parse(body);
        return (response.StatusCode, payload.RootElement.GetProperty("item").GetProperty("id").GetGuid());
    }

    /// <summary>Counts the DELETE statements a context sends.</summary>
    private sealed class DeleteCounter : DbCommandInterceptor
    {
        public int Statements { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("DELETE", StringComparison.OrdinalIgnoreCase)) Statements++;
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Scoped owner of the raced context, so the host's request scope disposes it.</summary>
    private sealed class RaceContext(TaskFlowDbContextTrxn db) : IAsyncDisposable
    {
        public TaskFlowDbContextTrxn Db { get; } = db;
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    /// <summary>
    /// When armed, commits a competing mapping for the armed key from a separate context just before a save that
    /// inserts that key, then disarms. Disarmed, it does nothing.
    /// </summary>
    private sealed class CompetingKey : SaveChangesInterceptor
    {
        private (Guid Tenant, string Scope, string Key, Guid Winner)? _armed;
        public bool Ran { get; private set; }

        public Guid Arm(Guid tenant, string scope, string key)
        {
            var winner = Guid.CreateVersion7();
            Ran = false;
            _armed = (tenant, scope, key, winner);
            return winner;
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (_armed is { } armed && eventData.Context!.ChangeTracker.Entries<IdempotencyKeyRecord>().Any(e =>
                    e.State == EntityState.Added && e.Entity.TenantId == armed.Tenant
                    && e.Entity.Scope == armed.Scope && e.Entity.Key == armed.Key))
            {
                _armed = null;
                await using var other = DbContainerFixture.CreateTrxnContext(_connectionString);
                other.IdempotencyKeys.Add(new IdempotencyKeyRecord
                {
                    EntityId = armed.Winner,
                    TenantId = armed.Tenant,
                    Scope = armed.Scope,
                    Key = armed.Key,
                    CreatedUtc = DateTimeOffset.UtcNow
                });
                await other.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, cancellationToken: cancellationToken);
                Ran = true;
            }

            return result;
        }
    }
}
