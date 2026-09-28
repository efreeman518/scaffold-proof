using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Domain.Shared.Enums;
using Test.Support;

namespace Test.Unit.Domain;

/// <summary>
/// Data-driven coverage of the <see cref="TaskItem.TransitionStatus"/> state machine: every legal transition
/// succeeds and every illegal transition produces a domain failure without changing the status.
/// Pure-unit tier: the aggregate decides transitions in-memory - zero infra cost is the right cost.
/// </summary>
[TestClass]
public class TaskItemStatusTransitionTests
{
    /// <summary>Verifies that given valid transition, when transitioned, then succeeds.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    [DataRow(TaskItemStatus.Open, TaskItemStatus.InProgress)]
    [DataRow(TaskItemStatus.Open, TaskItemStatus.Cancelled)]
    [DataRow(TaskItemStatus.InProgress, TaskItemStatus.Completed)]
    [DataRow(TaskItemStatus.InProgress, TaskItemStatus.Blocked)]
    [DataRow(TaskItemStatus.InProgress, TaskItemStatus.Cancelled)]
    [DataRow(TaskItemStatus.Blocked, TaskItemStatus.InProgress)]
    [DataRow(TaskItemStatus.Blocked, TaskItemStatus.Cancelled)]
    [DataRow(TaskItemStatus.Completed, TaskItemStatus.Open)]
    [DataRow(TaskItemStatus.Cancelled, TaskItemStatus.Open)]
    public void Given_ValidTransition_When_Transitioned_Then_Succeeds(TaskItemStatus current, TaskItemStatus target)
    {
        var task = CreateTaskAtStatus(current);

        var result = task.TransitionStatus(target);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(target, task.Status);
    }

    /// <summary>Verifies that given invalid transition, when transitioned, then fails and keeps the status.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    [DataRow(TaskItemStatus.Open, TaskItemStatus.Completed)]
    [DataRow(TaskItemStatus.Open, TaskItemStatus.Blocked)]
    [DataRow(TaskItemStatus.Completed, TaskItemStatus.InProgress)]
    [DataRow(TaskItemStatus.Completed, TaskItemStatus.Cancelled)]
    [DataRow(TaskItemStatus.Cancelled, TaskItemStatus.InProgress)]
    [DataRow(TaskItemStatus.Cancelled, TaskItemStatus.Completed)]
    [DataRow(TaskItemStatus.Blocked, TaskItemStatus.Completed)]
    [DataRow(TaskItemStatus.Blocked, TaskItemStatus.Open)]
    public void Given_InvalidTransition_When_Transitioned_Then_Fails(TaskItemStatus current, TaskItemStatus target)
    {
        var task = CreateTaskAtStatus(current);

        var result = task.TransitionStatus(target);

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(current, task.Status);
    }

    /// <summary>Verifies that given transition to none, when transitioned, then succeeds.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_TransitionToNone_When_Transitioned_Then_Succeeds()
    {
        var task = CreateTaskAtStatus(TaskItemStatus.Open);

        var result = task.TransitionStatus(TaskItemStatus.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(TaskItemStatus.None, task.Status);
    }

    /// <summary>Walks a new task through legal transitions to reach <paramref name="status"/>.</summary>
    private static TaskItem CreateTaskAtStatus(TaskItemStatus status)
    {
        var task = TaskItem.Create(TenantId.From(TestConstants.TenantId), "Transition sample").Value!;
        TaskItemStatus[] path = status switch
        {
            TaskItemStatus.Open => [],
            TaskItemStatus.InProgress => [TaskItemStatus.InProgress],
            TaskItemStatus.Blocked => [TaskItemStatus.InProgress, TaskItemStatus.Blocked],
            TaskItemStatus.Completed => [TaskItemStatus.InProgress, TaskItemStatus.Completed],
            TaskItemStatus.Cancelled => [TaskItemStatus.Cancelled],
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
        };

        foreach (var next in path)
            Assert.IsTrue(task.TransitionStatus(next).IsSuccess);

        return task;
    }
}
