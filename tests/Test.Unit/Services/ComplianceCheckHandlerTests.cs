using EF.FlowEngine.Abstractions;
using EF.FlowEngine.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using System.Text.Json;
using TaskFlow.Application.Contracts;
using TaskFlow.Bootstrapper;
using TaskFlow.Scheduler;
using TaskFlow.Scheduler.Handlers;

namespace Test.Unit.Services;

/// <summary>
/// Validates <see cref="ComplianceCheckHandler"/>, the start path of compliance-check (D-075): without the self-call
/// relay a start only for the tenant the workflow's API calls act for (the scaffold tenant), with the instance tenant and
/// the <c>tenantId</c> param both that tenant and <c>dueBefore</c> the run time plus the configured window, and one
/// Warning and no start for every other qualifying tenant, which does not fail the run; with the relay a start for every
/// qualifying tenant, each with its own tenant; an idempotency key that is stable for the UTC day; a same-day
/// re-run resolved by the engine to the day's instance without failing; and a failed start surfaced after every tenant
/// was handled. Pure-unit tier: the system repository and the engine are in-memory fakes;
/// the engine fake keeps FlowEngine's documented duplicate-key contract (the existing instance is returned).
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class ComplianceCheckHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 6, 10, 0, TimeSpan.Zero);
    private static readonly Guid Served = Guid.Parse(ScaffoldPrincipal.TenantId);
    private static readonly Guid OtherA = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid OtherB = Guid.Parse("00000000-0000-0000-0000-0000000000b2");

    private readonly FakeTaskItemSystemRepository _repo = new();
    private readonly KeyedFlowEngine _engine = new();
    private readonly RecordingLogger _logger = new();

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task HandleAsync_RelayConfigured_StartsEveryQualifyingTenant_WithItsOwnTenant()
    {
        _repo.ComplianceTenants.AddRange([OtherA, Served, OtherB]);

        await Handler(Now, windowDays: 7, relay: true).HandleAsync(TestContext.CancellationToken);

        CollectionAssert.AreEqual(new[] { OtherA.ToString(), Served.ToString(), OtherB.ToString() },
            _engine.Requests.Select(r => r.TenantId).ToArray());
        Assert.IsTrue(_engine.Requests.TrueForAll(r => Param(r, "tenantId") == r.TenantId), "each instance's tenantId param is its own tenant");
        CollectionAssert.AreEqual(new[] { $"compliance-check:{OtherA}:2026-10-06", $"compliance-check:{Served}:2026-10-06", $"compliance-check:{OtherB}:2026-10-06" },
            _engine.Requests.Select(r => r.IdempotencyKey).ToArray());
        Assert.IsFalse(_logger.Entries.Any(e => e.Level == LogLevel.Warning), "no tenant is skipped");
        var summary = _logger.Entries.Single(e => e.Message.StartsWith("Compliance check: ", StringComparison.Ordinal)).Message;
        StringAssert.Contains(summary, "3 tenants");
        StringAssert.Contains(summary, "3 started");
        StringAssert.Contains(summary, "0 not started");
    }

    [TestMethod]
    public async Task HandleAsync_StartsTheServedTenant_WithThatTenantAndTheWindow()
    {
        _repo.ComplianceTenants.AddRange([OtherA, Served, OtherB]);

        await Handler(Now, windowDays: 7).HandleAsync(TestContext.CancellationToken);

        var dueBefore = Now.AddDays(7);
        CollectionAssert.AreEqual(new[] { ("compliance", dueBefore) }, _repo.ComplianceQueries.ToArray());
        var request = _engine.Requests.Single();
        Assert.AreEqual("compliance-check", request.WorkflowId);
        Assert.AreEqual(Served.ToString(), request.TenantId, "the instance carries the tenant, so its evidence reads are allowed");
        Assert.AreEqual(Served.ToString(), Param(request, "tenantId"), "the tenantId param names the same tenant");
        Assert.AreEqual("2026-10-13T06:10:00.0000000+00:00", Param(request, "dueBefore"));
        Assert.AreEqual(dueBefore, DateTimeOffset.Parse(Param(request, "dueBefore"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreEqual($"compliance-check:{Served}:2026-10-06", request.IdempotencyKey);
        CollectionAssert.AreEquivalent(new[] { "tenantId", "dueBefore" }, request.Params!.Keys.ToArray(),
            "exactly the workflow's paramsSchema");
    }

    [TestMethod]
    public async Task HandleAsync_OtherTenants_AreNotStarted_OneWarningEach_AndTheRunSucceeds()
    {
        _repo.ComplianceTenants.AddRange([OtherA, Served, OtherB]);

        await Handler(Now, windowDays: 7).HandleAsync(TestContext.CancellationToken);

        Assert.IsFalse(_engine.Requests.Any(r => r.TenantId != Served.ToString()), "no instance for a tenant the API calls cannot read");
        var warnings = _logger.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList();
        Assert.HasCount(2, warnings);
        StringAssert.Contains(warnings[0], OtherA.ToString());
        StringAssert.Contains(warnings[1], OtherB.ToString());
        Assert.IsTrue(warnings.TrueForAll(w => w.Contains(ScaffoldPrincipal.TenantId, StringComparison.Ordinal)), "the reason names the identity");
        Assert.IsTrue(warnings.TrueForAll(w => w.Contains("FlowEngine:SelfCall:TokenScope", StringComparison.Ordinal)), "and the setting that lifts it");
        var summary = _logger.Entries.Single(e => e.Message.StartsWith("Compliance check: ", StringComparison.Ordinal)).Message;
        StringAssert.Contains(summary, "3 tenants");
        StringAssert.Contains(summary, "1 started");
        StringAssert.Contains(summary, "2 not started");
        StringAssert.Contains(summary, "0 failed");
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
        _repo.ComplianceTenants.Add(Served);

        await Handler(Now, windowDays: 3).HandleAsync(TestContext.CancellationToken);

        Assert.AreEqual(Now.AddDays(3), _repo.ComplianceQueries.Single().DueBefore);
        Assert.AreEqual(Now.AddDays(3).ToString("O", System.Globalization.CultureInfo.InvariantCulture), Param(_engine.Requests.Single(), "dueBefore"));
    }

    [TestMethod]
    public void IdempotencyKey_IsStableForTheUtcDay_AndChangesTheNextDay()
    {
        var early = ComplianceCheckHandler.IdempotencyKey(Served, new DateTimeOffset(2026, 10, 6, 0, 0, 1, TimeSpan.Zero));
        var late = ComplianceCheckHandler.IdempotencyKey(Served, new DateTimeOffset(2026, 10, 6, 23, 59, 59, TimeSpan.Zero));
        // 2026-10-07 01:00 at +05:00 is still 2026-10-06 in UTC.
        var offset = ComplianceCheckHandler.IdempotencyKey(Served, new DateTimeOffset(2026, 10, 7, 1, 0, 0, TimeSpan.FromHours(5)));
        var nextDay = ComplianceCheckHandler.IdempotencyKey(Served, new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero));

        Assert.AreEqual($"compliance-check:{Served}:2026-10-06", early);
        Assert.AreEqual(early, late);
        Assert.AreEqual(early, offset, "the key uses the UTC date of the run");
        Assert.AreEqual($"compliance-check:{Served}:2026-10-07", nextDay);
        Assert.AreNotEqual(early, ComplianceCheckHandler.IdempotencyKey(OtherB, Now), "each tenant has its own key");
    }

    [TestMethod]
    public async Task HandleAsync_SameDayRerun_ResolvesToTheDaysInstance_AndIsNotAFailure()
    {
        _repo.ComplianceTenants.AddRange([Served, OtherB]);

        await Handler(Now, windowDays: 7).HandleAsync(TestContext.CancellationToken);
        await Handler(Now.AddHours(10), windowDays: 7).HandleAsync(TestContext.CancellationToken);

        CollectionAssert.AreEqual(
            new[] { $"compliance-check:{Served}:2026-10-06", $"compliance-check:{Served}:2026-10-06" },
            _engine.Requests.Select(r => r.IdempotencyKey).ToArray(),
            "each run asks the engine with the day's key");
        Assert.HasCount(1, _engine.Instances, "the engine resolves the day's key to one instance");
        Assert.AreEqual(_engine.Returned[0].InstanceId, _engine.Returned[1].InstanceId);
        StringAssert.Contains(_logger.Entries.Last().Message, "1 started or already started today");
        StringAssert.Contains(_logger.Entries.Last().Message, "0 failed");
    }

    [TestMethod]
    public void IdempotencyKey_FitsTheStateStoresCorrelationIdColumn()
    {
        // The key is also the instance's correlation id; the store's column is 64 characters wide.
        Assert.HasCount(64, ComplianceCheckHandler.IdempotencyKey(Served, Now));
        Assert.AreEqual(
            ComplianceCheckHandler.IdempotencyKey(Served, Now),
            ComplianceCheckHandler.StartRequestFor(Served, Now, Now.AddDays(7)).CorrelationId);
    }

    [TestMethod]
    public async Task HandleAsync_FailedStart_SurfacesAfterEveryTenantIsHandled()
    {
        var primary = new InvalidOperationException("state store unavailable");
        _repo.ComplianceTenants.AddRange([Served, OtherB]);
        _engine.FailFor[Served.ToString()] = primary;

        var thrown = await Assert.ThrowsExactlyAsync<AggregateException>(
            () => Handler(Now, windowDays: 7).HandleAsync(TestContext.CancellationToken));

        var failure = thrown.InnerExceptions.Single();
        StringAssert.Contains(failure.Message, Served.ToString(), "the failure names its tenant");
        Assert.AreSame(primary, failure.InnerException, "the primary error is kept");
        Assert.IsTrue(_logger.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains(OtherB.ToString(), StringComparison.Ordinal)),
            "the tenant after the failure is still handled");
        StringAssert.Contains(_logger.Entries.Last().Message, "1 failed");
    }

    [TestMethod]
    public async Task HandleAsync_Cancelled_StopsAtOnce()
    {
        using var cts = new CancellationTokenSource();
        _repo.ComplianceTenants.AddRange([Served, OtherB]);
        _engine.OnStart = cts.Cancel;

        await Assert.ThrowsAsync<OperationCanceledException>(() => Handler(Now, windowDays: 7).HandleAsync(cts.Token));

        Assert.HasCount(1, _engine.Requests);
        Assert.IsFalse(_logger.Entries.Any(e => e.Level == LogLevel.Warning), "no further tenant is handled after cancellation");
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

    private ComplianceCheckHandler Handler(DateTimeOffset now, int windowDays, bool relay = false) => new(
        _repo,
        _engine.Object,
        SchedulerTestTelemetry.Create(),
        new FixedTimeProvider(now),
        Options.Create(new ComplianceCheckSettings { WindowDays = windowDays }),
        Options.Create(new SelfCallRelayOptions { TokenScope = relay ? "api://taskflow-api/.default" : "" }),
        _logger);

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
                    CorrelationId = request.CorrelationId,
                    Status = ExecStatus.Running,
                };
                Instances[request.IdempotencyKey!] = instance;
            }

            Returned.Add(instance);
            return instance;
        }
    }

    /// <summary>Records each formatted log entry with its level.</summary>
    private sealed class RecordingLogger : ILogger<ComplianceCheckHandler>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
