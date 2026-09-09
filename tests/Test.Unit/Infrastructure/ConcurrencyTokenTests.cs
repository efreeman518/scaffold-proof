using EF.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Data.Interceptors;
using Test.Support;
using Test.Support.Builders;

namespace Test.Unit.Infrastructure;

/// <summary>
/// D-021: the app-managed Version token works on the InMemory provider, so the 412 path is unit-testable
/// without a database (a rowversion never could be).
/// </summary>
[TestClass]
public sealed class ConcurrencyTokenTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task Save_StampsVersionAndTimestamps_OnInsertAndUpdate()
    {
        var ct = TestContext.CancellationToken;
        var dbName = Guid.NewGuid().ToString();
        var clock = new FixedTimeProvider(Start);
        var tag = new TagBuilder().WithName("v1").Build();

        await using (var db = Create(dbName, clock))
        {
            db.Tags.Add(tag);
            await db.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: ct);
        }

        Assert.AreEqual(1, tag.Version);
        Assert.AreEqual(Start, tag.CreatedAtUtc);
        Assert.AreEqual(Start, tag.ModifiedAtUtc);

        clock.Now = Start.AddMinutes(5);
        await using (var db = Create(dbName, clock))
        {
            var loaded = await db.Tags.SingleAsync(t => t.Id == tag.Id, ct);
            loaded.Update(name: "v2");
            await db.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: ct);

            Assert.AreEqual(2, loaded.Version);
            Assert.AreEqual(Start, loaded.CreatedAtUtc);
            Assert.AreEqual(Start.AddMinutes(5), loaded.ModifiedAtUtc);
        }
    }

    [TestMethod]
    public async Task Save_WithStaleVersion_ThrowsDbUpdateConcurrencyException()
    {
        var ct = TestContext.CancellationToken;
        var dbName = Guid.NewGuid().ToString();
        var tag = new TagBuilder().WithName("original").Build();
        await using (var seed = Create(dbName))
        {
            seed.Tags.Add(tag);
            await seed.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: ct);
        }

        // Two editors load the same row (Version 1).
        await using var first = Create(dbName);
        await using var second = Create(dbName);
        var firstCopy = await first.Tags.SingleAsync(t => t.Id == tag.Id, ct);
        var secondCopy = await second.Tags.SingleAsync(t => t.Id == tag.Id, ct);

        firstCopy.Update(name: "first wins");
        await first.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: ct);
        Assert.AreEqual(2, firstCopy.Version);

        // The stale copy still carries original Version 1; EF's WHERE Version = 1 matches nothing.
        // Throw (not the package ClientWins retry) so the conflict surfaces to the caller.
        secondCopy.Update(name: "second loses");
        await Assert.ThrowsExactlyAsync<DbUpdateConcurrencyException>(
            () => second.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, cancellationToken: ct));
    }

    public TestContext TestContext { get; set; } = null!;

    private static TaskFlowDbContextTrxn Create(string dbName, TimeProvider? clock = null) =>
        new(new DbContextOptionsBuilder<TaskFlowDbContextTrxn>()
            .UseInMemoryDatabase(dbName)
            .AddInterceptors(new VersionTimestampInterceptor(clock))
            .Options)
        {
            AuditId = "concurrency-test",
            TenantId = TestConstants.TenantId
        };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
