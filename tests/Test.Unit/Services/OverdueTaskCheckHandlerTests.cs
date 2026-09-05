using Microsoft.Extensions.Logging.Abstractions;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Domain.Shared.Events;
using TaskFlow.Observability.Meters;
using TaskFlow.Scheduler.Handlers;
using Test.Support;

namespace Test.Unit.Services;

/// <summary>
/// Validates <see cref="OverdueTaskCheckHandler"/>: one transaction per tenant, the announcement staged only
/// when the guarded update actually marked rows, and a message id that is a pure function of
/// (tenant, task, due date) so a re-run or a second replica stages the same row rather than a duplicate.
/// Pure-unit tier: the system repository and the outbox are in-memory fakes.
/// </summary>
[TestClass]
public class OverdueTaskCheckHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantA = TestConstants.TenantId;
    private static readonly Guid TenantB = Guid.Parse("00000000-0000-0000-0000-000000000099");

    private readonly FakeTaskItemSystemRepository _repo = new();
    private readonly FakeOutboxStaging _outbox = new();
    private readonly OverdueTaskCheckHandler _handler;

    /// <summary>Initializes overdue task check handler tests with required dependencies and default state.</summary>
    public OverdueTaskCheckHandlerTests()
    {
        _handler = new OverdueTaskCheckHandler(
            _repo,
            _outbox,
            new SchedulerJobMeter(),
            new FixedTimeProvider(Now),
            NullLogger<OverdueTaskCheckHandler>.Instance);
    }

    /// <summary>Each tenant is marked and announced inside its own transaction.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task HandleAsync_GroupsCandidatesByTenant_OneTransactionEach()
    {
        var a1 = Guid.CreateVersion7();
        var a2 = Guid.CreateVersion7();
        var b1 = Guid.CreateVersion7();
        _repo.OverdueRows.AddRange(
        [
            new OverdueTaskRow(TenantA, a1, Now.AddDays(-3)),
            new OverdueTaskRow(TenantA, a2, Now.AddDays(-1)),
            new OverdueTaskRow(TenantB, b1, Now.AddDays(-5))
        ]);

        await _handler.HandleAsync(TestContext.CancellationToken);

        Assert.AreEqual(2, _repo.TransactionCount);
        Assert.AreEqual(2, _repo.MarkedOverdue.Count);
        CollectionAssert.AreEquivalent(new[] { a1, a2 }, _repo.MarkedOverdue[0].Ids.ToArray());
        CollectionAssert.AreEquivalent(new[] { b1 }, _repo.MarkedOverdue[1].Ids.ToArray());
        Assert.AreEqual(3, _outbox.Staged.Count);
        Assert.IsTrue(_outbox.Staged.TrueForAll(s => s.Envelope.Type == nameof(TaskItemOverdueSuspectedEvent)));
    }

    /// <summary>The guarded update runs before staging, and the save closes the same transaction.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task HandleAsync_MarksBeforeStaging_InsideOneTransaction()
    {
        _repo.OverdueRows.Add(new OverdueTaskRow(TenantA, Guid.CreateVersion7(), Now.AddDays(-2)));

        await _handler.HandleAsync(TestContext.CancellationToken);

        CollectionAssert.AreEqual(
            new[]
            {
                nameof(FakeTaskItemSystemRepository.StreamOverdueAsync),
                "BeginTransaction",
                nameof(FakeTaskItemSystemRepository.MarkOverdueNotifiedAsync),
                nameof(FakeTaskItemSystemRepository.SaveChangesAsync),
                "Commit"
            },
            _repo.Calls);
    }

    /// <summary>A batch whose rows all lost the race stages nothing: no announcement without a marked row.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task HandleAsync_WhenNothingMarked_StagesNoEvent()
    {
        _repo.OverdueRows.Add(new OverdueTaskRow(TenantA, Guid.CreateVersion7(), Now.AddDays(-2)));
        _repo.MarkedOverride = 0;

        await _handler.HandleAsync(TestContext.CancellationToken);

        Assert.AreEqual(0, _outbox.Staged.Count);
        Assert.AreEqual(0, _repo.SaveCount);
    }

    /// <summary>
    /// Two runs over the same candidate stage the same message id, so a replayed job collapses onto one outbox
    /// row. A different due date is a different announcement and gets a different id.
    /// </summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task HandleAsync_MessageId_IsStableForTheSameDueDate()
    {
        var taskId = Guid.Parse("0199e3f0-0000-7000-8000-0000000000aa");
        _repo.OverdueRows.Add(new OverdueTaskRow(TenantA, taskId, Now.AddDays(-2)));

        await _handler.HandleAsync(TestContext.CancellationToken);
        await _handler.HandleAsync(TestContext.CancellationToken);

        Assert.AreEqual(2, _outbox.Staged.Count);
        Assert.AreEqual(_outbox.Staged[0].DeterministicId, _outbox.Staged[1].DeterministicId);
        Assert.AreEqual(_outbox.Staged[0].Envelope.Id, _outbox.Staged[0].DeterministicId);

        var rescheduled = new FakeTaskItemSystemRepository();
        var otherOutbox = new FakeOutboxStaging();
        rescheduled.OverdueRows.Add(new OverdueTaskRow(TenantA, taskId, Now.AddDays(-1)));
        var handler = new OverdueTaskCheckHandler(
            rescheduled, otherOutbox, new SchedulerJobMeter(), new FixedTimeProvider(Now),
            NullLogger<OverdueTaskCheckHandler>.Instance);

        await handler.HandleAsync(TestContext.CancellationToken);

        Assert.AreNotEqual(_outbox.Staged[0].DeterministicId, otherOutbox.Staged[0].DeterministicId);
    }

    public TestContext TestContext { get; set; } = null!;
}
