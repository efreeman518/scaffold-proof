using EF.Cache;
using Test.Integration.Infrastructure;

namespace Test.Integration;

/// <summary>
/// D-052 against a real Redis: exclusion and expiry are both server-side behaviors that no in-process test
/// can prove. The expiry case is the one that matters operationally - it is what stops a replica that died
/// mid-provisioning from blocking every other replica forever.
/// Component tier: the shared Redis Testcontainer; two lock instances stand in for two replicas.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public class RedisDistributedLockTests
{
    /// <summary>MSTest-injected context; supplies the per-test cancellation token.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>Marks the test Inconclusive when the Redis container failed to start.</summary>
    [TestInitialize]
    public void TestSetup() => IntegrationTestSetup.AssertAvailable("Redis", RedisContainerFixture.StartupError);

    /// <summary>Two replicas, one key: the first wins, the second wins only once the first releases.</summary>
    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public async Task TryAcquireAsync_TwoReplicas_SecondWinsAfterRelease()
    {
        var key = $"taskflow:test:lock:{Guid.NewGuid():N}";
        using var replicaA = NewLock();
        using var replicaB = NewLock();

        var held = await replicaA.TryAcquireAsync(key, TimeSpan.FromMinutes(1), TestContext.CancellationToken);
        Assert.IsNotNull(held);

        var contended = await replicaB.TryAcquireAsync(key, TimeSpan.FromMinutes(1), TestContext.CancellationToken);
        Assert.IsNull(contended, "the second replica must not hold a lock the first one owns");

        await held.DisposeAsync();

        var afterRelease = await replicaB.TryAcquireAsync(key, TimeSpan.FromMinutes(1), TestContext.CancellationToken);
        Assert.IsNotNull(afterRelease, "the compare-and-delete release must free the key");
        await afterRelease.DisposeAsync();
    }

    /// <summary>An unreleased lock expires on its ttl, so a crashed holder cannot block the next replica.</summary>
    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public async Task TryAcquireAsync_UnreleasedLock_ExpiresOnItsTtl()
    {
        var key = $"taskflow:test:lock:{Guid.NewGuid():N}";
        using var crashedReplica = NewLock();
        using var nextReplica = NewLock();

        // Never disposed on purpose: this is the crashed-holder case.
        var abandoned = await crashedReplica.TryAcquireAsync(
            key, TimeSpan.FromSeconds(1), TestContext.CancellationToken);
        Assert.IsNotNull(abandoned);

        IAsyncDisposable? acquired = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (acquired is null && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(250, TestContext.CancellationToken);
            acquired = await nextReplica.TryAcquireAsync(
                key, TimeSpan.FromSeconds(30), TestContext.CancellationToken);
        }

        Assert.IsNotNull(acquired, "the ttl must free a lock its holder never released");
        await acquired.DisposeAsync();
    }

    /// <summary>A release by a non-owner is a no-op, so an expired holder cannot free the next owner's lock.</summary>
    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public async Task DisposeAsync_ByAnExpiredHolder_DoesNotFreeTheNewOwnersLock()
    {
        var key = $"taskflow:test:lock:{Guid.NewGuid():N}";
        using var expiredHolder = NewLock();
        using var newOwner = NewLock();

        var stale = await expiredHolder.TryAcquireAsync(key, TimeSpan.FromSeconds(1), TestContext.CancellationToken);
        Assert.IsNotNull(stale);

        IAsyncDisposable? owned = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (owned is null && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(250, TestContext.CancellationToken);
            owned = await newOwner.TryAcquireAsync(key, TimeSpan.FromMinutes(1), TestContext.CancellationToken);
        }

        Assert.IsNotNull(owned);

        // The stale holder now releases the key it no longer owns. Compare-and-delete is what makes this a
        // no-op; a plain DEL here would silently hand the same lock to a third replica.
        await stale.DisposeAsync();

        var stillHeld = await expiredHolder.TryAcquireAsync(key, TimeSpan.FromMinutes(1), TestContext.CancellationToken);
        Assert.IsNull(stillHeld, "the stale release must not have freed the new owner's lock");

        await owned.DisposeAsync();
    }

    private static RedisDistributedLock NewLock() => new(RedisContainerFixture.ConnectionString);
}
