using EF.FlowEngine.Abstractions;
using TaskFlow.Bootstrapper;

namespace Test.Unit.Infrastructure;

/// <summary>
/// The FlowEngine lease decorator: a second claim by the process claimant for an instance this process already
/// holds is refused (the SQL provider would grant it, so the start path and the sweep both executed one instance),
/// until it is released, lost on renewal or expired; other claimants and the other members pass through.
/// Pure unit: an in-memory inner provider that, like the SQL one, grants a held lease to the same claimant again.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class ExclusiveInProcessLockProviderTests
{
    private const string Process = "process-claimant";
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Given_AHeldLease_When_TheProcessClaimsItAgain_Then_TheClaimIsRefusedUntilReleased()
    {
        var (provider, inner, _) = Create();
        var ct = TestContext.CancellationToken;

        Assert.IsTrue(await provider.TryAcquireAsync("i1", Process, Lease, ct));
        Assert.IsFalse(await provider.TryAcquireAsync("i1", Process, Lease, ct), "the sweep must not run an instance the start path holds");
        Assert.IsTrue(await inner.TryAcquireAsync("i1", Process, Lease, ct), "precondition: the inner provider alone grants it again");
        Assert.IsTrue(await provider.TryAcquireAsync("i2", Process, Lease, ct), "another instance is independent");

        await provider.ReleaseAsync("i1", Process, ct);
        Assert.IsTrue(await provider.TryAcquireAsync("i1", Process, Lease, ct), "a released lease can be claimed again");
    }

    [TestMethod]
    public async Task Given_ConcurrentClaims_When_TheProcessClaimsOneInstance_Then_ExactlyOneWins()
    {
        var (provider, _, _) = Create();
        var ct = TestContext.CancellationToken;

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => provider.TryAcquireAsync("i1", Process, Lease, ct), ct)));

        Assert.AreEqual(1, results.Count(r => r));
    }

    [TestMethod]
    public async Task Given_AHeldLease_When_ItExpiresOrIsLostOnRenewal_Then_ItCanBeClaimedAgain()
    {
        var (provider, inner, clock) = Create();
        var ct = TestContext.CancellationToken;

        Assert.IsTrue(await provider.TryAcquireAsync("i1", Process, Lease, ct));
        clock.Advance(Lease - TimeSpan.FromSeconds(1));
        Assert.IsTrue(await provider.RenewAsync("i1", Process, Lease, ct));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.IsFalse(await provider.TryAcquireAsync("i1", Process, Lease, ct), "a renewal extends the in-process hold");
        clock.Advance(Lease);
        Assert.IsTrue(await provider.TryAcquireAsync("i1", Process, Lease, ct), "an expired hold is taken over, as a crashed lease is");

        inner.RenewSucceeds = false;
        Assert.IsFalse(await provider.RenewAsync("i1", Process, Lease, ct));
        Assert.IsTrue(await provider.TryAcquireAsync("i1", Process, Lease, ct), "a lease lost on renewal is free");
    }

    [TestMethod]
    public async Task Given_AnotherClaimant_When_ItClaims_Then_TheInnerProviderDecides()
    {
        var (provider, inner, _) = Create();
        var ct = TestContext.CancellationToken;

        Assert.IsTrue(await provider.TryAcquireAsync("i1", "child-signal-c1", Lease, ct));
        Assert.IsTrue(await provider.TryAcquireAsync("i1", "child-signal-c1", Lease, ct), "only the process claimant is made exclusive");
        inner.AcquireSucceeds = false;
        Assert.IsFalse(await provider.TryAcquireAsync("i2", Process, Lease, ct));
        inner.AcquireSucceeds = true;
        Assert.IsTrue(await provider.TryAcquireAsync("i2", Process, Lease, ct), "a refused inner claim leaves no in-process hold");
    }

    private static (ExclusiveInProcessLockProvider Provider, ReentrantProvider Inner, ManualClock Clock) Create()
    {
        var inner = new ReentrantProvider();
        var clock = new ManualClock();
        return (new ExclusiveInProcessLockProvider(inner, Process, clock), inner, clock);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class ReentrantProvider : IDistributedLockProvider
    {
        public bool AcquireSucceeds { get; set; } = true;
        public bool RenewSucceeds { get; set; } = true;

        public Task<bool> TryAcquireAsync(string instanceId, string claimantId, TimeSpan leaseDuration, CancellationToken ct = default) =>
            Task.FromResult(AcquireSucceeds);

        public Task<bool> RenewAsync(string instanceId, string claimantId, TimeSpan leaseDuration, CancellationToken ct = default) =>
            Task.FromResult(RenewSucceeds);

        public Task ReleaseAsync(string instanceId, string claimantId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> FindResumableInstanceIdsAsync(
            DateTimeOffset now, int batchSize, int partitionCount = 1, int partitionIndex = 0, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
