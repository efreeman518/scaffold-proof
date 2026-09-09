using Microsoft.Extensions.Logging.Abstractions;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Model.ValueObjects;
using TaskFlow.Domain.Shared;
using TaskFlow.Domain.Shared.Enums;
using TaskFlow.Domain.Shared.Events;
using TaskFlow.Observability.Meters;
using TaskFlow.Scheduler.Handlers;
using Test.Support;

namespace Test.Unit.Services;

/// <summary>
/// Validates <see cref="RecurringTaskGenerationHandler"/>: the guarded advance is the lock (nothing is written
/// when another replica already moved the template), occurrence ids are a pure function of
/// (tenant, template, occurrence), and each generated occurrence gets its created event staged - the upsert
/// path never runs the domain-event interceptor.
/// Pure-unit tier: the system repository and the outbox are in-memory fakes.
/// </summary>
[TestClass]
public class RecurringTaskGenerationHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantA = TestConstants.TenantId;

    private readonly FakeTaskItemSystemRepository _repo = new();
    private readonly FakeOutboxStaging _outbox = new();
    private readonly RecurringTaskGenerationHandler _handler;

    /// <summary>Initializes recurring task generation handler tests with required dependencies and default state.</summary>
    public RecurringTaskGenerationHandlerTests()
    {
        _handler = new RecurringTaskGenerationHandler(
            _repo,
            _outbox,
            new SchedulerJobMeter(),
            new FixedTimeProvider(Now),
            NullLogger<RecurringTaskGenerationHandler>.Instance);
    }

    /// <summary>Every due occurrence becomes an upserted task plus one staged created event.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task HandleAsync_ExpandsDueOccurrences_AndStagesOneEventEach()
    {
        _repo.DueTemplates.Add(Template(Now.AddDays(-3), RecurrencePattern.Daily, interval: 1));

        await _handler.HandleAsync(TestContext.CancellationToken);

        // -3d, -2d, -1d and today are all <= now.
        Assert.AreEqual(4, _repo.UpsertedOccurrences.Count);
        Assert.AreEqual(4, _outbox.Staged.Count);
        Assert.IsTrue(_outbox.Staged.TrueForAll(s => s.Envelope.Type == nameof(TaskItemCreatedEvent)));
        Assert.IsTrue(_repo.UpsertedOccurrences.TrueForAll(o => o.RecurrenceTemplateId is not null));
    }

    /// <summary>The advance runs first, so a template another replica already moved writes nothing.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task HandleAsync_WhenAdvanceLosesTheRace_WritesNothing()
    {
        _repo.DueTemplates.Add(Template(Now.AddDays(-3), RecurrencePattern.Daily, interval: 1));
        _repo.AdvanceResult = false;

        await _handler.HandleAsync(TestContext.CancellationToken);

        Assert.AreEqual(0, _repo.UpsertedOccurrences.Count);
        Assert.AreEqual(0, _outbox.Staged.Count);
        Assert.AreEqual(0, _repo.SaveCount);
    }

    /// <summary>The advance is guarded on the pointer the run read, and moves it past the last occurrence.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task HandleAsync_AdvancesFromTheObservedPointer_ToTheNextOccurrence()
    {
        var dueFrom = Now.AddDays(-1);
        _repo.DueTemplates.Add(Template(dueFrom, RecurrencePattern.Weekly, interval: 1));

        await _handler.HandleAsync(TestContext.CancellationToken);

        Assert.AreEqual(1, _repo.Advances.Count);
        var advance = _repo.Advances[0];
        Assert.AreEqual(dueFrom, advance.Guard);
        Assert.IsTrue(advance.Next == dueFrom.AddDays(7), $"expected {dueFrom.AddDays(7):O}, got {advance.Next:O}");
    }

    /// <summary>A pattern past its end date generates nothing and clears the pointer so it leaves the due set.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task HandleAsync_PastEndDate_StopsTheSeries()
    {
        _repo.DueTemplates.Add(Template(Now.AddDays(-1), RecurrencePattern.Daily, interval: 1, endDate: Now.AddDays(-5)));

        await _handler.HandleAsync(TestContext.CancellationToken);

        Assert.AreEqual(0, _repo.UpsertedOccurrences.Count);
        Assert.IsNull(_repo.Advances[0].Next);
    }

    /// <summary>An unusable frequency is reported and taken out of the due set rather than retried forever.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task HandleAsync_UnsupportedFrequency_ClearsThePointer()
    {
        _repo.DueTemplates.Add(Template(Now.AddDays(-1), "Fortnightly", interval: 1));

        await _handler.HandleAsync(TestContext.CancellationToken);

        Assert.AreEqual(0, _repo.UpsertedOccurrences.Count);
        Assert.AreEqual(1, _repo.Advances.Count);
        Assert.IsNull(_repo.Advances[0].Next);
    }

    /// <summary>The same occurrence always gets the same id, so a replayed run upserts onto the same rows.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task HandleAsync_OccurrenceIds_AreStableAcrossRuns()
    {
        _repo.DueTemplates.Add(Template(Now.AddDays(-2), RecurrencePattern.Daily, interval: 1));

        await _handler.HandleAsync(TestContext.CancellationToken);
        var firstRun = _repo.UpsertedOccurrences.ConvertAll(o => o.Id.Value);
        _repo.UpsertedOccurrences.Clear();

        await _handler.HandleAsync(TestContext.CancellationToken);
        var secondRun = _repo.UpsertedOccurrences.ConvertAll(o => o.Id.Value);

        CollectionAssert.AreEqual(firstRun, secondRun);
        Assert.AreEqual(firstRun.Count, firstRun.Distinct().Count());
    }

    /// <summary>A far-behind template catches up at most 12 occurrences per run.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task HandleAsync_BacklogIsCappedPerRun()
    {
        _repo.DueTemplates.Add(Template(Now.AddDays(-100), RecurrencePattern.Daily, interval: 1));

        await _handler.HandleAsync(TestContext.CancellationToken);

        Assert.AreEqual(RecurrencePattern.MaxOccurrencesPerRun, _repo.UpsertedOccurrences.Count);
    }

    /// <summary>Builds a recurring template positioned at <paramref name="nextOccurrenceAtUtc"/>.</summary>
    private static TaskItem Template(
        DateTimeOffset nextOccurrenceAtUtc, string frequency, int interval, DateTimeOffset? endDate = null)
    {
        var template = TaskItem.Create(
            DomainId.From<TenantId>(TenantA), "Weekly report", "template", Priority.Medium).Value!;
        template.Update(features: TaskFeatures.Recurring);
        template.UpdateDateRange(null, nextOccurrenceAtUtc);
        template.UpdateRecurrencePattern(new RecurrencePattern
        {
            Frequency = frequency,
            Interval = interval,
            EndDate = endDate
        });
        return template;
    }

    public TestContext TestContext { get; set; } = null!;
}
