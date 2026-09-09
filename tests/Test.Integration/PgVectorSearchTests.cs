using EF.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Infrastructure.AI.Search;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Data.Provider;
using TaskFlow.Infrastructure.Data.ReadModel;
using TaskFlow.Infrastructure.Repositories;
using Test.Integration.Infrastructure;

namespace Test.Integration;

/// <summary>
/// D-040 pgvector search projection against a real database. Everything asserted here is provider behavior
/// that cannot be reasoned about from the C#: whether the migration installs the extension and the HNSW index,
/// whether the FlexLabs upsert reaches ON CONFLICT with a vector column, and whether cosine ordering actually
/// ranks the nearer vector first.
/// <para>
/// The suite is provider-split on purpose. On the PostgreSql lane it exercises the arm; on the SqlServer lane
/// it asserts the other half of the same decision - that the entity is absent from the model - which is what
/// keeps the SQL Server migration snapshot free of a column SQL Server cannot create.
/// </para>
/// Component tier: contexts directly against the standalone database Testcontainer.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public class PgVectorSearchTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();

    /// <summary>Marks the test Inconclusive when the database container failed to start.</summary>
    [TestInitialize]
    public void TestSetup() => IntegrationTestSetup.AssertAvailable("SQL", DbContainerFixture.StartupError);

    /// <summary>
    /// SqlServer lane: the one forced provider branch has to stay one-sided. A DbSet or an unconditional
    /// configuration would put the vector column into this model and into its migration snapshot.
    /// </summary>
    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task SqlServerLane_DoesNotMapTheEmbeddingEntity()
    {
        if (DbContainerFixture.Provider != TaskFlowDbProvider.SqlServer) Assert.Inconclusive("PostgreSql lane.");

        await using var db = DbContainerFixture.CreateQueryContext();
        Assert.IsNull(
            db.Model.FindEntityType(typeof(TaskItemEmbedding)),
            "TaskItemEmbedding must not enter the SQL Server model - its migration snapshot would gain a vector column.");
    }

    /// <summary>PostgreSql lane: the migration installs the extension, the table and the HNSW cosine index.</summary>
    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task PostgreSqlLane_MigrationCreatesTheVectorExtensionAndHnswIndex()
    {
        if (DbContainerFixture.Provider != TaskFlowDbProvider.PostgreSql) Assert.Inconclusive("SqlServer lane.");

        var ct = TestContext.CancellationToken;
        var connString = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync("pgvector");
        await MigrateAsync(connString, ct);

        await using var db = DbContainerFixture.CreateQueryContext(connString);
        Assert.IsNotNull(db.Model.FindEntityType(typeof(TaskItemEmbedding)));

        var extensions = await db.Database
            .SqlQuery<string>($"SELECT extname AS \"Value\" FROM pg_extension")
            .ToListAsync(ct);
        CollectionAssert.Contains(extensions, "vector", "the migration must install the pgvector extension");

        var indexes = await db.Database
            .SqlQuery<string>($"SELECT indexdef AS \"Value\" FROM pg_indexes WHERE tablename = 'TaskItemEmbedding'")
            .ToListAsync(ct);
        Assert.IsTrue(
            indexes.Any(d => d.Contains("hnsw", StringComparison.OrdinalIgnoreCase)
                             && d.Contains("vector_cosine_ops", StringComparison.OrdinalIgnoreCase)),
            $"expected an HNSW cosine index, saw: {string.Join(" | ", indexes)}");
    }

    /// <summary>
    /// PostgreSql lane: two tasks embedded, and a query embedded closer to one of them. The nearer task has to
    /// come back first, and a replayed upsert has to replace its row rather than fail on the primary key.
    /// </summary>
    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task PostgreSqlLane_SemanticSearchRanksTheNearerTaskFirst_AndUpsertIsReplayable()
    {
        if (DbContainerFixture.Provider != TaskFlowDbProvider.PostgreSql) Assert.Inconclusive("SqlServer lane.");

        var ct = TestContext.CancellationToken;
        var connString = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync("pgvector");
        await MigrateAsync(connString, ct);

        var databaseTaskId = await SeedTaskAsync(connString, "Migrate the database", ct);
        var designTaskId = await SeedTaskAsync(connString, "Redesign the marketing page", ct);

        var generator = new StubEmbeddingGenerator();
        await using (var scope = NewScope(connString))
        {
            await scope.Repository.UpsertAsync(
                TenantId, databaseTaskId, StubEmbeddingGenerator.DatabaseVector, StubEmbeddingGenerator.ModelName,
                DateTimeOffset.UtcNow, ct);
            await scope.Repository.UpsertAsync(
                TenantId, designTaskId, StubEmbeddingGenerator.DesignVector, StubEmbeddingGenerator.ModelName,
                DateTimeOffset.UtcNow, ct);
            // Replay: ON CONFLICT DO UPDATE, not a duplicate-key failure.
            await scope.Repository.UpsertAsync(
                TenantId, databaseTaskId, StubEmbeddingGenerator.DatabaseVector, StubEmbeddingGenerator.ModelName,
                DateTimeOffset.UtcNow, ct);
        }

        await using (var scope = NewScope(connString))
        {
            var rows = await scope.Read.Set<TaskItemEmbedding>().AsNoTracking()
                .Where(e => e.TenantId == TenantId).ToListAsync(ct);
            Assert.AreEqual(2, rows.Count, "the replayed upsert must replace, not duplicate");
            Assert.IsTrue(rows.All(r => r.Dimensions == StubEmbeddingGenerator.Dimensions));
            Assert.IsTrue(rows.All(r => r.ModelId == StubEmbeddingGenerator.ModelName));

            var search = new PgVectorSearchService(
                scope.Repository,
                generator,
                new NoOpSearchService(null!, NullLogger<NoOpSearchService>.Instance));

            var results = await search.SearchTaskItemsAsync(
                StubEmbeddingGenerator.DatabaseQuery, SearchMode.Semantic, TenantId, maxResults: 10, ct);

            Assert.AreEqual(2, results.Count);
            Assert.AreEqual(databaseTaskId.ToString(), results[0].Id, "the nearer vector must rank first");
            Assert.AreEqual("Migrate the database", results[0].Title);
            Assert.IsTrue(results[0].Score > results[1].Score, "Score must fall as cosine distance grows");
        }

        // Another tenant sees none of it (GR-19).
        await using (var scope = NewScope(connString))
        {
            var others = await scope.Repository.SearchNearestAsync(
                Guid.CreateVersion7(), StubEmbeddingGenerator.DatabaseVector, 10, ct);
            Assert.AreEqual(0, others.Count);
        }
    }

    /// <summary>PostgreSql lane: a task deleted after its event was staged loses its embedding row.</summary>
    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task PostgreSqlLane_DeleteRemovesTheRow_AndIsANoOpWhenAbsent()
    {
        if (DbContainerFixture.Provider != TaskFlowDbProvider.PostgreSql) Assert.Inconclusive("SqlServer lane.");

        var ct = TestContext.CancellationToken;
        var connString = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync("pgvector");
        await MigrateAsync(connString, ct);
        var taskId = await SeedTaskAsync(connString, "Temporary task", ct);

        await using var scope = NewScope(connString);
        await scope.Repository.UpsertAsync(
            TenantId, taskId, StubEmbeddingGenerator.DatabaseVector, StubEmbeddingGenerator.ModelName,
            DateTimeOffset.UtcNow, ct);

        await scope.Repository.DeleteAsync(TenantId, taskId, ct);
        await scope.Repository.DeleteAsync(TenantId, taskId, ct);

        Assert.AreEqual(0, await scope.Read.Set<TaskItemEmbedding>().CountAsync(e => e.TenantId == TenantId, ct));
    }

    public TestContext TestContext { get; set; } = null!;

    private static async Task MigrateAsync(string connString, CancellationToken ct)
    {
        await using var db = DbContainerFixture.CreateTrxnContext(connString);
        await db.Database.MigrateAsync(ct);
    }

    private static async Task<Guid> SeedTaskAsync(string connString, string title, CancellationToken ct)
    {
        await using var db = DbContainerFixture.CreateTrxnContext(connString);
        db.TenantId = TenantId;
        var task = TaskItem.Create(DomainId.From<TaskFlow.Domain.Shared.TenantId>(TenantId), title).Value!;
        db.Add(task);
        await db.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: ct);
        return task.Id.Value;
    }

    private static RepositoryScope NewScope(string connString)
    {
        var write = DbContainerFixture.CreateTrxnContext(connString);
        var read = DbContainerFixture.CreateQueryContext(connString);
        return new RepositoryScope(write, read, new PgVectorTaskEmbeddingRepository(write, read));
    }

    private sealed record RepositoryScope(
        TaskFlowDbContextTrxn Write,
        TaskFlowDbContextQuery Read,
        ITaskEmbeddingRepository Repository) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Write.DisposeAsync();
            await Read.DisposeAsync();
        }
    }

    /// <summary>
    /// Deterministic generator: the assertion is about cosine ordering, not about a model. The query vector is
    /// deliberately closer to <see cref="DatabaseVector"/> than to <see cref="DesignVector"/>.
    /// </summary>
    private sealed class StubEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public const string ModelName = "stub-embed";

        /// <summary>Must match the deployed column type: pgvector rejects any other length outright.</summary>
        public const int Dimensions = TaskItemEmbedding.DefaultDimensions;

        public const string DatabaseQuery = "database migration";

        // Two orthogonal unit vectors and a query leaning heavily toward the first, so the expected ranking
        // follows from the geometry rather than from anything the search service does.
        public static readonly float[] DatabaseVector = Axis(0, 1f);
        public static readonly float[] DesignVector = Axis(1, 1f);
        private static readonly float[] QueryVector = Axis(0, 0.9f, 1, 0.1f);

        private static float[] Axis(int index, float value, int? secondIndex = null, float secondValue = 0f)
        {
            var vector = new float[Dimensions];
            vector[index] = value;
            if (secondIndex is int i) vector[i] = secondValue;
            return vector;
        }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                [.. values.Select(_ => new Embedding<float>(QueryVector) { ModelId = ModelName })]));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
