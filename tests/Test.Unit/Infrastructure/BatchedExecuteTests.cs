using EF.Data.Contracts;

namespace Test.Unit.Infrastructure;

/// <summary>
/// Validates the retention batching loop, now EF.Data.Contracts' (package request 10). The point of the helper
/// is that one sweep has a hard ceiling on how long it can hold the database, so the two properties worth
/// pinning are the statement count for a known row count and the stop conditions: a short batch ends the sweep,
/// and the batch ceiling ends it by throwing rather than reporting a complete sweep that is not one.
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

    /// <summary>
    /// The ceiling holds and is reported: a backlog larger than maxBatches issues exactly maxBatches
    /// statements and then throws, so a caller cannot mistake a truncated sweep for a finished one. The
    /// app-local helper this replaced returned the partial total silently, which hid a retention window
    /// that never drained.
    /// </summary>
    [TestMethod]
    public async Task RunBatchedAsync_AtMaxBatches_ThrowsRatherThanReportingAFinishedSweep()
    {
        var statements = 0;

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            BatchedExecute.RunBatchedAsync((size, _) =>
            {
                statements++;
                return Task.FromResult(size);
            }, batchSize: 100, maxBatches: 5, ct: TestContext.CancellationToken));

        Assert.AreEqual(5, statements);
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
