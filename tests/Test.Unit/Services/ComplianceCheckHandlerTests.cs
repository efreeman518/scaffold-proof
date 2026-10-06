using EF.FlowEngine.Abstractions;
using EF.FlowEngine.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Text.Json;
using TaskFlow.Scheduler;
using TaskFlow.Scheduler.Handlers;

namespace Test.Unit.Services;

/// <summary>
/// Validates <see cref="ComplianceCheckHandler"/>, the start path of compliance-check (D-075): one start per tenant the
/// system repository streams, the instance tenant and the <c>tenantId</c> param both that tenant, <c>dueBefore</c> the
/// run time plus the configured window, an idempotency key that is stable for the UTC day, a duplicate start resolved to
/// the existing instance without failing, and any other failure surfaced after every tenant was attempted.
/// Pure-unit tier: the system repository and the engine are in-memory fakes; the engine fake keeps FlowEngine's
/// documented duplicate-key contract (the existing instance is returned, nothing is thrown).
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class ComplianceCheckHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 6, 10, 0, TimeSpan.Zero);
    private static readonly Guid TenantA = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid TenantB = Guid.Parse("00000000-0000-0000-0000-0000000000b2");

    private readonly FakeTaskItemSystemRepository _repo = new();
    private readonly KeyedFlowEngine _engine = new();

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task HandleAsync_StartsOnePerStreamedTenant_WithThatTenantAndTheWindow()
    {
        _repo.ComplianceTenants.AddRange([TenantA, TenantB]);

        await Handler(Now, windowDays: 7).HandleAsync(TestContext.CancellationToken);

        var dueBefore = Now.AddDays(7);
        CollectionAssert.AreEqual(new[] { ("compliance", dueBefore) }, _repo.ComplianceQueries.ToArray());
        Assert.HasCount(2, _engine.Requests);
        foreach (var (request, tenant) in _engine.Requests.Zip(new[] { TenantA, TenantB }))
        {
            Assert.AreEqual("compliance-check", request.WorkflowId);
            Assert.AreEqual(tenant.ToString(), request.TenantId, "the instance carries the tenant, so its evidence reads are allowed");
            Assert.AreEqual(tenant.ToString(), Param(request, "tenantId"), "the tenantId param names the same tenant");
            Assert.AreEqual("2026-10-13T06:10:00.0000000+00:00", Param(request, "dueBefore"));
            Assert.AreEqual(dueBefore, DateTimeOffset.Parse(Param(request, "dueBefore"), System.Globalization.CultureInfo.InvariantCulture));
            Assert.AreEqual($"compliance-check:{tenant}:2026-10-06", request.IdempotencyKey);
            CollectionAssert.AreEquivalent(new[] { "tenantId", "dueBefore" }, request.Params!.Keys.ToArray(),
                "exactly the workflow's paramsSchema");
        }
    }

    [TestMethod]
    public async Task HandleAsync_NoTenantWithADueComplianceTask_StartsNothing()
    {
        await Handler(Now, windowDays: 7).HandleAsync(TestContext.CancellationToken);

        Assert.IsEmpty(_engine.Requests);
        Assert.HasCount(1, _repo.ComplianceQueries);
    }

    [TestMethod]
    public async Task HandleAsync_WindowDays_SetsTheDueHorizon()
    {
        _repo.ComplianceTenants.Add(TenantA);

        await Handler(Now, windowDays: 3).HandleAsync(TestContext.CancellationToken);

        Assert.AreEqual(Now.AddDays(3), _repo.ComplianceQueries.Single().DueBefore);
        Assert.AreEqual(Now.AddDays(3).ToString("O", System.Globalization.CultureInfo.InvariantCulture), Param(_engine.Requests.Single(), "dueBefore"));
    }

    [TestMethod]
    public void IdempotencyKey_IsStableForTheUtcDay_AndChangesTheNextDay()
    {
        var early = ComplianceCheckHandler.IdempotencyKey(TenantA, new DateTimeOffset(2026, 10, 6, 0, 0, 1, TimeSpan.Zero));
        var late = ComplianceCheckHandler.IdempotencyKey(TenantA, new DateTimeOffset(2026, 10, 6, 23, 59, 59, TimeSpan.Zero));
        // 2026-10-07 01:00 at +05:00 is still 2026-10-06 in UTC.
        var offset = ComplianceCheckHandler.IdempotencyKey(TenantA, new DateTimeOffset(2026, 10, 7, 1, 0, 0, TimeSpan.FromHours(5)));
        var nextDay = ComplianceCheckHandler.IdempotencyKey(TenantA, new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero));

        Assert.AreEqual($"compliance-check:{TenantA}:2026-10-06", early);
        Assert.AreEqual(early, late);
        Assert.AreEqual(early, offset, "the key uses the UTC date of the run");
        Assert.AreEqual($"compliance-check:{TenantA}:2026-10-07", nextDay);
        Assert.AreNotEqual(early, ComplianceCheckHandler.IdempotencyKey(TenantB, Now), "each tenant has its own key");
    }

    [TestMethod]
    public async Task HandleAsync_SameDayRerun_ResolvesToTheExistingInstances_WithoutFailing()
    {
        _repo.ComplianceTenants.AddRange([TenantA, TenantB]);

        await Handler(Now, windowDays: 7).HandleAsync(TestContext.CancellationToken);
        await Handler(Now.AddHours(10), windowDays: 7).HandleAsync(TestContext.CancellationToken);

        Assert.HasCount(4, _engine.Requests, "the rerun asks again");
        Assert.HasCount(2, _engine.Instances, "but the engine holds one instance per tenant for the day");
        CollectionAssert.AreEqual(
            _engine.Returned.Take(2).Select(i => i.InstanceId).ToArray(),
            _engine.Returned.Skip(2).Select(i => i.InstanceId).ToArray(),
            "the rerun resolves to the morning's instances");
    }

    [TestMethod]
    public async Task HandleAsync_OneTenantFails_TheOthersStart_AndTheFailureSurfaces()
    {
        var primary = new InvalidOperationException("state store unavailable");
        _repo.ComplianceTenants.AddRange([TenantA, TenantB]);
        _engine.FailFor[TenantA.ToString()] = primary;

        var thrown = await Assert.ThrowsExactlyAsync<AggregateException>(
            () => Handler(Now, windowDays: 7).HandleAsync(TestContext.CancellationToken));

        Assert.AreEqual(TenantB.ToString(), _engine.Returned.Single().TenantId, "the failed tenant does not hold back the next one");
        var failure = thrown.InnerExceptions.Single();
        StringAssert.Contains(failure.Message, TenantA.ToString(), "the failure names its tenant");
        Assert.AreSame(primary, failure.InnerException, "the primary error is kept");
    }

    [TestMethod]
    public async Task HandleAsync_Cancelled_StopsAtOnce()
    {
        using var cts = new CancellationTokenSource();
        _repo.ComplianceTenants.AddRange([TenantA, TenantB]);
        _engine.OnStart = cts.Cancel;

        await Assert.ThrowsAsync<OperationCanceledException>(() => Handler(Now, windowDays: 7).HandleAsync(cts.Token));

        Assert.HasCount(1, _engine.Requests, "no further tenant is started after cancellation");
    }

    [TestMethod]
    [DataRow("0", false)]
    [DataRow("1", true)]
    [DataRow("365", true)]
    [DataRow("366", false)]
    public void Registration_ValidatesWindowDays(string windowDays, bool valid)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Scheduling:Compliance:WindowDays"] = windowDays })
            .Build();
        using var provider = new ServiceCollection().AddSchedulerServices(config).BuildServiceProvider();

        var read = () => provider.GetRequiredService<IOptions<ComplianceCheckSettings>>().Value;

        if (valid) Assert.AreEqual(int.Parse(windowDays, System.Globalization.CultureInfo.InvariantCulture), read().WindowDays);
        else Assert.ThrowsExactly<OptionsValidationException>(() => read());
    }

    [TestMethod]
    public void Registration_DefaultsWindowDaysTo7()
    {
        using var provider = new ServiceCollection().AddSchedulerServices(new ConfigurationBuilder().Build()).BuildServiceProvider();

        Assert.AreEqual(7, provider.GetRequiredService<IOptions<ComplianceCheckSettings>>().Value.WindowDays);
    }

    private ComplianceCheckHandler Handler(DateTimeOffset now, int windowDays) => new(
        _repo,
        _engine.Object,
        SchedulerTestTelemetry.Create(),
        new FixedTimeProvider(now),
        Options.Create(new ComplianceCheckSettings { WindowDays = windowDays }),
        NullLogger<ComplianceCheckHandler>.Instance);

    private static string Param(StartRequest request, string name) =>
        ((JsonContextValue)request.Params![name]).Value.GetString()!;

    /// <summary>
    /// An engine that keeps FlowEngine's start contract for <see cref="StartRequest.IdempotencyKey"/>: a key it already
    /// holds returns that instance instead of creating a new one.
    /// </summary>
    private sealed class KeyedFlowEngine
    {
        private readonly Mock<IFlowEngine> _mock = new(MockBehavior.Strict);

        public KeyedFlowEngine()
        {
            _mock.Setup(e => e.StartBackgroundAsync(It.IsAny<StartRequest>(), It.IsAny<CancellationToken>()))
                .Returns((StartRequest request, CancellationToken ct) => Task.FromResult(Start(request, ct)));
        }

        public IFlowEngine Object => _mock.Object;
        public List<StartRequest> Requests { get; } = [];
        public Dictionary<string, ExecutionInstance> Instances { get; } = [];
        public List<ExecutionInstance> Returned { get; } = [];
        public Dictionary<string, Exception> FailFor { get; } = [];
        public Action? OnStart { get; set; }

        private ExecutionInstance Start(StartRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            OnStart?.Invoke();
            ct.ThrowIfCancellationRequested();
            if (FailFor.TryGetValue(request.TenantId!, out var failure)) throw failure;

            if (!Instances.TryGetValue(request.IdempotencyKey!, out var instance))
            {
                instance = new ExecutionInstance
                {
                    InstanceId = Guid.CreateVersion7().ToString(),
                    WorkflowId = request.WorkflowId,
                    TenantId = request.TenantId,
                    Status = ExecStatus.Running,
                };
                Instances[request.IdempotencyKey!] = instance;
            }

            Returned.Add(instance);
            return instance;
        }
    }
}
