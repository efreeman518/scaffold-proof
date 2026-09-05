using TaskFlow.Infrastructure.Repositories;

namespace Test.Unit.Infrastructure;

/// <summary>
/// Validates the retention batching loop. The point of the helper is that one sweep has a hard ceiling on how
/// long it can hold the database, so the two properties worth pinning are the statement count for a known row
/// count and the stop conditions: a short batch ends the sweep, and maxBatches ends it regardless.
/// Pure-unit tier: the loop is exercised through a delegate, so no database is involved.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class BatchedExecuteTests
{
    /// <summary>2500 rows at 1000 per batch: three statements, 2500 rows reported.</summary>
    [TestMethod]
    public async Task RunBatchedAsync_2500Rows_Issues3Statements()
    {
        var remaining = 2500;
        var statements = 0;

        var total = await BatchedExecute.RunBatchedAsync((size, _) =>
        {
            statements++;
            var affected = Math.Min(size, remaining);
            remaining -= affected;
            return Task.FromResult(affected);
        }, batchSize: 1000, maxBatches: 100, ct: TestContext.CancellationToken);

        Assert.AreEqual(3, statements);
        Assert.AreEqual(2500, total);
    }

    /// <summary>An exactly-full final batch costs one extra statement to learn the window is empty.</summary>
    [TestMethod]
    public async Task RunBatchedAsync_ExactMultiple_ProbesOnceMore()
    {
        var remaining = 2000;
        var statements = 0;

        var total = await BatchedExecute.RunBatchedAsync((size, _) =>
        {
            statements++;
            var affected = Math.Min(size, remaining);
            remaining -= affected;
            return Task.FromResult(affected);
        }, batchSize: 1000, maxBatches: 100, ct: TestContext.CancellationToken);

        Assert.AreEqual(3, statements);
        Assert.AreEqual(2000, total);
    }

    /// <summary>The ceiling holds: a backlog larger than maxBatches leaves the rest for the next run.</summary>
    [TestMethod]
    public async Task RunBatchedAsync_StopsAtMaxBatches()
    {
        var statements = 0;

        var total = await BatchedExecute.RunBatchedAsync((size, _) =>
        {
            statements++;
            return Task.FromResult(size);
        }, batchSize: 100, maxBatches: 5, ct: TestContext.CancellationToken);

        Assert.AreEqual(5, statements);
        Assert.AreEqual(500, total);
    }

    /// <summary>An empty window costs exactly one statement.</summary>
    [TestMethod]
    public async Task RunBatchedAsync_NothingToDelete_IssuesOneStatement()
    {
        var statements = 0;

        var total = await BatchedExecute.RunBatchedAsync((_, _) =>
        {
            statements++;
            return Task.FromResult(0);
        }, batchSize: 1000, maxBatches: 100, ct: TestContext.CancellationToken);

        Assert.AreEqual(1, statements);
        Assert.AreEqual(0, total);
    }

    public TestContext TestContext { get; set; } = null!;
}
