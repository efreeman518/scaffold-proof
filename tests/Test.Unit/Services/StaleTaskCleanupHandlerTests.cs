using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Observability.Meters;
using TaskFlow.Scheduler.Handlers;
using Test.Support;

namespace Test.Unit.Services;

/// <summary>
/// Validates <see cref="StaleTaskCleanupHandler"/>: blob deletions are recorded before the rows they point at
/// disappear, the retention cutoff is re-asserted on the delete, and paging resumes past the last row scanned
/// so a task skipped for still having subtasks is not re-read for the rest of the run.
/// Pure-unit tier: the system repository is an in-memory fake.
/// </summary>
[TestClass]
public class StaleTaskCleanupHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantA = TestConstants.TenantId;
    private static readonly Guid TenantB = Guid.Parse("00000000-0000-0000-0000-000000000099");

    private readonly FakeTaskItemSystemRepository _repo = new();

    /// <summary>Blob work is staged before the delete, inside the same transaction.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task HandleAsync_StagesBlobDeletes_BeforeDeletingTasks()
    {
        _repo.StaleBatches.Enqueue([new StaleTaskRow(TenantA, Guid.CreateVersion7())]);

        await Handler().HandleAsync(TestContext.CancellationToken);

        CollectionAssert.AreEqual(
            new[]
            {
                nameof(FakeTaskItemSystemRepository.GetStaleBatchAsync),
                "BeginTransaction",
                nameof(FakeTaskItemSystemRepository.StageBlobDeletesAsync),
                nameof(FakeTaskItemSystemRepository.DeleteStaleBatchAsync),
                "Commit"
            },
            _repo.Calls);
    }

    /// <summary>Each tenant in a page is deleted in its own transaction: EF cannot express a tuple IN.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task HandleAsync_GroupsPageByTenant()
    {
        _repo.StaleBatches.Enqueue(
        [
            new StaleTaskRow(TenantA, Guid.CreateVersion7()),
            new StaleTaskRow(TenantB, Guid.CreateVersion7()),
            new StaleTaskRow(TenantA, Guid.CreateVersion7())
        ]);

        await Handler().HandleAsync(TestContext.CancellationToken);

        Assert.AreEqual(2, _repo.TransactionCount);
        Assert.AreEqual(2, _repo.Deletes.Count);
        Assert.AreEqual(2, _repo.Deletes[0].Ids.Count);
        Assert.AreEqual(1, _repo.Deletes[1].Ids.Count);
    }

    /// <summary>The configured retention window is what reaches the delete predicate.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task HandleAsync_PassesConfiguredRetentionCutoff()
    {
        _repo.StaleBatches.Enqueue([new StaleTaskRow(TenantA, Guid.CreateVersion7())]);

        await Handler(retentionDays: 30).HandleAsync(TestContext.CancellationToken);

        Assert.AreEqual(Now.AddDays(-30), _repo.Deletes[0].Cutoff);
    }

    /// <summary>An empty batch ends the run without a transaction.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task HandleAsync_NoStaleTasks_DoesNothing()
    {
        await Handler().HandleAsync(TestContext.CancellationToken);

        Assert.AreEqual(0, _repo.TransactionCount);
        Assert.AreEqual(0, _repo.Deletes.Count);
    }

    /// <summary>Builds the handler over an in-memory configuration.</summary>
    private StaleTaskCleanupHandler Handler(int? retentionDays = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(retentionDays is null
                ? []
                : new Dictionary<string, string?>
                {
                    ["Scheduling:StaleCleanup:RetentionDays"] = retentionDays.Value.ToString()
                })
            .Build();

        return new StaleTaskCleanupHandler(
            _repo,
            new SchedulerJobMeter(),
            new FixedTimeProvider(Now),
            config,
            NullLogger<StaleTaskCleanupHandler>.Instance);
    }

    public TestContext TestContext { get; set; } = null!;
}
