using TaskFlow.Domain.Model;
using TaskFlow.Domain.Model.ValueObjects;
using TaskFlow.Domain.Shared;
using TaskFlow.Domain.Shared.Constants;
using TaskFlow.Domain.Shared.Enums;

namespace Test.Mutation.Domain;

/// <summary>
/// Mutation tests are normal MSTest tests. Stryker.NET mutates the configured target project
/// and reruns this filtered MSTest suite to decide which mutants are killed or survived.
/// Run the suite from repo root:
/// <code>
/// dotnet tool restore
/// dotnet test tests/Test.Mutation/Test.Mutation.csproj
/// </code>
/// Then run Stryker from tests/Test.Mutation:
/// <code>
/// dotnet tool run dotnet-stryker
/// </code>
/// The HTML mutation report is written under StrykerOutput.
/// </summary>
[TestClass]
[TestCategory("Mutation")]
public class TaskItemMutationSamples
{
    private static readonly TenantId TenantId = TenantId.From(Guid.Parse("8d955af4-9444-45d6-90b6-8f4572611d82"));

    /// <summary>Verifies that given title at minimum length, when task item created, then succeeds.</summary>
    [TestMethod]
    public void Given_TitleAtMinimumLength_When_TaskItemCreated_Then_Succeeds()
    {
        var title = new string('a', DomainConstants.RULE_DEFAULT_NAME_LENGTH_MIN);

        var result = TaskItem.Create(TenantId, title);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(title, result.Value!.Title);
    }

    /// <summary>Verifies that given title below minimum length, when task item created, then fails with minimum length message.</summary>
    [TestMethod]
    public void Given_TitleBelowMinimumLength_When_TaskItemCreated_Then_FailsWithMinimumLengthMessage()
    {
        var title = new string('a', DomainConstants.RULE_DEFAULT_NAME_LENGTH_MIN - 1);

        var result = TaskItem.Create(TenantId, title);

        Assert.IsTrue(result.IsFailure);
        Assert.Contains(
            $"Title must be at least {DomainConstants.RULE_DEFAULT_NAME_LENGTH_MIN} characters.",
            string.Join(";", result.Errors));
    }

    /// <summary>Verifies that given whitespace title, when task item created, then fails with required title message.</summary>
    [TestMethod]
    public void Given_WhitespaceTitle_When_TaskItemCreated_Then_FailsWithRequiredTitleMessage()
    {
        var result = TaskItem.Create(TenantId, "   ");

        Assert.IsTrue(result.IsFailure);
        Assert.Contains("Title is required.", string.Join(";", result.Errors));
    }

    /// <summary>Verifies that given empty tenant ID, when task item created, then fails with tenant message.</summary>
    [TestMethod]
    public void Given_EmptyTenantId_When_TaskItemCreated_Then_FailsWithTenantMessage()
    {
        var result = TaskItem.Create(TenantId.From(Guid.Empty), "Mutation sample");

        Assert.IsTrue(result.IsFailure);
        Assert.Contains("Tenant ID cannot be empty.", string.Join(";", result.Errors));
    }

    /// <summary>Verifies that given allowed aggregate transition, when task item transitioned, then status changes.</summary>
    [TestMethod]
    [DataRow(TaskItemStatus.Open, TaskItemStatus.InProgress)]
    [DataRow(TaskItemStatus.Open, TaskItemStatus.Cancelled)]
    [DataRow(TaskItemStatus.InProgress, TaskItemStatus.Completed)]
    [DataRow(TaskItemStatus.InProgress, TaskItemStatus.Blocked)]
    [DataRow(TaskItemStatus.InProgress, TaskItemStatus.Cancelled)]
    [DataRow(TaskItemStatus.Blocked, TaskItemStatus.InProgress)]
    [DataRow(TaskItemStatus.Blocked, TaskItemStatus.Cancelled)]
    [DataRow(TaskItemStatus.Completed, TaskItemStatus.Open)]
    [DataRow(TaskItemStatus.Cancelled, TaskItemStatus.Open)]
    public void Given_AllowedAggregateTransition_When_TaskItemTransitioned_Then_StatusChanges(
        TaskItemStatus current,
        TaskItemStatus target)
    {
        var task = CreateTaskAtStatus(current);

        var result = task.TransitionStatus(target);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(target, task.Status);
    }

    /// <summary>Verifies that given disallowed status transition, when task item transitioned, then fails and keeps status.</summary>
    [TestMethod]
    [DataRow(TaskItemStatus.Open, TaskItemStatus.Blocked)]
    [DataRow(TaskItemStatus.Completed, TaskItemStatus.Blocked)]
    [DataRow(TaskItemStatus.Cancelled, TaskItemStatus.Completed)]
    public void Given_DisallowedStatusTransition_When_TaskItemTransitioned_Then_FailsAndKeepsStatus(
        TaskItemStatus current,
        TaskItemStatus target)
    {
        var task = CreateTaskAtStatus(current);

        var result = task.TransitionStatus(target);

        Assert.IsTrue(result.IsFailure);
        Assert.Contains($"Cannot transition from {current} to {target}.", string.Join(";", result.Errors));
        Assert.AreEqual(current, task.Status);
    }

    /// <summary>Verifies that given disallowed aggregate transition, when task item transitioned, then fails with transition message.</summary>
    [TestMethod]
    public void Given_DisallowedAggregateTransition_When_TaskItemTransitioned_Then_FailsWithTransitionMessage()
    {
        var task = TaskItem.Create(TenantId, "Mutation sample").Value!;

        var result = task.TransitionStatus(TaskItemStatus.Blocked);

        Assert.IsTrue(result.IsFailure);
        Assert.Contains("Cannot transition from Open to Blocked.", string.Join(";", result.Errors));
        Assert.AreEqual(TaskItemStatus.Open, task.Status);
    }

    /// <summary>Verifies that given completed task, when reopened, then completed date cleared.</summary>
    [TestMethod]
    public void Given_CompletedTask_When_Reopened_Then_CompletedDateCleared()
    {
        var task = TaskItem.Create(TenantId, "Mutation sample").Value!;
        Assert.IsTrue(task.TransitionStatus(TaskItemStatus.InProgress).IsSuccess);
        Assert.IsTrue(task.TransitionStatus(TaskItemStatus.Completed).IsSuccess);
        Assert.IsNotNull(task.CompletedDate);

        var result = task.TransitionStatus(TaskItemStatus.Open);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(TaskItemStatus.Open, task.Status);
        Assert.IsNull(task.CompletedDate);
    }

    /// <summary>Verifies that given completed task, when reset to none, then status and completed date reset.</summary>
    [TestMethod]
    public void Given_CompletedTask_When_ResetToNone_Then_StatusAndCompletedDateReset()
    {
        var task = TaskItem.Create(TenantId, "Mutation sample").Value!;
        Assert.IsTrue(task.TransitionStatus(TaskItemStatus.InProgress).IsSuccess);
        Assert.IsTrue(task.TransitionStatus(TaskItemStatus.Completed).IsSuccess);
        Assert.IsNotNull(task.CompletedDate);

        var result = task.TransitionStatus(TaskItemStatus.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(TaskItemStatus.None, task.Status);
        Assert.IsNull(task.CompletedDate);
    }

    /// <summary>Verifies that given existing tag, when associated again, then tag association is idempotent.</summary>
    [TestMethod]
    public void Given_ExistingTag_When_AssociatedAgain_Then_TagAssociationIsIdempotent()
    {
        var task = TaskItem.Create(TenantId, "Mutation sample").Value!;
        var tagId = TagId.From(Guid.Parse("3f3f9966-753b-43f5-bba7-54e87fbaf101"));

        var first = task.AssociateTag(tagId);
        var second = task.AssociateTag(tagId);

        Assert.IsTrue(first.IsSuccess);
        Assert.IsTrue(second.IsSuccess);
        Assert.AreSame(first.Value, second.Value);
        Assert.HasCount(1, task.TaskItemTags);
    }

    /// <summary>Verifies that given all update values, when task updated, then fields and optional links change.</summary>
    [TestMethod]
    public void Given_AllUpdateValues_When_TaskUpdated_Then_FieldsAndOptionalLinksChange()
    {
        var originalCategoryId = CategoryId.From(Guid.Parse("f92d0bd5-935b-432f-bd21-89b924b9eb87"));
        var originalParentId = TaskItemId.From(Guid.Parse("2a44e0d1-fd9e-4989-95a2-95e9cf8e8217"));
        var newParentId = TaskItemId.From(Guid.Parse("ee7e6b25-4cb9-4238-8cbb-743f2ce4ee0f"));
        var task = TaskItem.Create(
            TenantId,
            "Original title",
            "Original description",
            Priority.Low,
            originalCategoryId,
            originalParentId).Value!;

        var result = task.Update(
            title: "Updated title",
            description: "Updated description",
            priority: Priority.Critical,
            features: TaskFeatures.Recurring | TaskFeatures.Reminder,
            estimatedEffort: 3.5m,
            actualEffort: 2.25m,
            categoryId: CategoryId.From(Guid.Empty),
            parentTaskItemId: newParentId);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("Updated title", task.Title);
        Assert.AreEqual("Updated description", task.Description);
        Assert.AreEqual(Priority.Critical, task.Priority);
        Assert.AreEqual(TaskFeatures.Recurring | TaskFeatures.Reminder, task.Features);
        Assert.AreEqual(3.5m, task.EstimatedEffort);
        Assert.AreEqual(2.25m, task.ActualEffort);
        Assert.IsNull(task.CategoryId);
        Assert.AreEqual(newParentId, task.ParentTaskItemId!.Value);
    }

    /// <summary>Verifies that given optional links, when update uses empty guid, then links are cleared independently.</summary>
    [TestMethod]
    public void Given_OptionalLinks_When_UpdateUsesEmptyGuid_Then_LinksAreClearedIndependently()
    {
        var categoryId = CategoryId.From(Guid.Parse("721e91c8-8b5c-4247-8c36-fd661da0deaa"));
        var parentId = TaskItemId.From(Guid.Parse("82b87a46-6e80-4df8-a242-4ebdcf86d584"));
        var task = TaskItem.Create(TenantId, "Mutation sample", categoryId: categoryId, parentTaskItemId: parentId).Value!;
        var replacementCategoryId = CategoryId.From(Guid.Parse("7b18b783-2554-4d68-9220-588667e3e8c2"));

        var result = task.Update(categoryId: replacementCategoryId, parentTaskItemId: TaskItemId.From(Guid.Empty));

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(replacementCategoryId, task.CategoryId!.Value);
        Assert.IsNull(task.ParentTaskItemId);
    }

    /// <summary>Verifies that given invalid updated title, when task updated, then fails with required title message.</summary>
    [TestMethod]
    public void Given_InvalidUpdatedTitle_When_TaskUpdated_Then_FailsWithRequiredTitleMessage()
    {
        var task = TaskItem.Create(TenantId, "Mutation sample").Value!;

        var result = task.Update(title: "   ");

        Assert.IsTrue(result.IsFailure);
        Assert.Contains("Title is required.", string.Join(";", result.Errors));
    }

    /// <summary>Verifies that given valid and invalid comments, when mutated, then collection state is explicit.</summary>
    [TestMethod]
    public void Given_ValidAndInvalidComments_When_Mutated_Then_CollectionStateIsExplicit()
    {
        var task = TaskItem.Create(TenantId, "Mutation sample").Value!;

        var invalid = task.AddComment("   ");
        var first = task.AddComment("First comment");

        Assert.IsTrue(invalid.IsFailure);
        Assert.IsTrue(first.IsSuccess);
        Assert.HasCount(1, task.Comments);

        Assert.IsTrue(task.RemoveComment(first.Value!).IsSuccess);
        Assert.IsEmpty(task.Comments);

        var second = task.AddComment("Second comment").Value!;

        Assert.IsTrue(task.RemoveComment(second.Id).IsSuccess);
        Assert.IsEmpty(task.Comments);
        Assert.IsTrue(task.RemoveComment(CommentId.From(Guid.Parse("34ddf67b-b0ac-4337-92f8-206372c58b33"))).IsSuccess);
    }

    /// <summary>Verifies that given valid and invalid checklist items, when mutated, then collection state is explicit.</summary>
    [TestMethod]
    public void Given_ValidAndInvalidChecklistItems_When_Mutated_Then_CollectionStateIsExplicit()
    {
        var task = TaskItem.Create(TenantId, "Mutation sample").Value!;

        var invalid = task.AddChecklistItem("   ");
        var first = task.AddChecklistItem("First checklist item", sortOrder: 10);

        Assert.IsTrue(invalid.IsFailure);
        Assert.IsTrue(first.IsSuccess);
        Assert.HasCount(1, task.ChecklistItems);
        Assert.AreEqual(10, first.Value!.SortOrder);

        Assert.IsTrue(task.RemoveChecklistItem(first.Value).IsSuccess);
        Assert.IsEmpty(task.ChecklistItems);

        var second = task.AddChecklistItem("Second checklist item").Value!;

        Assert.IsTrue(task.RemoveChecklistItem(second.Id).IsSuccess);
        Assert.IsEmpty(task.ChecklistItems);
        Assert.IsTrue(task.RemoveChecklistItem(ChecklistItemId.From(Guid.Parse("d6f2f7ca-f98a-48c4-ab13-d735fe67004a"))).IsSuccess);
    }

    /// <summary>Verifies that given tag associations, when removed by object and ID, then collection state is explicit.</summary>
    [TestMethod]
    public void Given_TagAssociations_When_RemovedByObjectAndId_Then_CollectionStateIsExplicit()
    {
        var task = TaskItem.Create(TenantId, "Mutation sample").Value!;
        var firstTagId = TagId.From(Guid.Parse("08e7611a-6b35-4b8b-a451-29a1340d1215"));
        var secondTagId = TagId.From(Guid.Parse("80875aa9-e0af-4712-842f-136445c5e759"));

        var first = task.AssociateTag(firstTagId).Value!;
        Assert.HasCount(1, task.TaskItemTags);

        Assert.IsTrue(task.RemoveTag(first).IsSuccess);
        Assert.IsEmpty(task.TaskItemTags);

        Assert.IsTrue(task.AssociateTag(secondTagId).IsSuccess);
        Assert.IsTrue(task.RemoveTag(secondTagId).IsSuccess);
        Assert.IsEmpty(task.TaskItemTags);
        Assert.IsTrue(task.RemoveTag(TagId.From(Guid.Parse("db295052-ae8a-417c-befe-b5cb92334589"))).IsSuccess);
    }

    /// <summary>Verifies that given date range and recurrence pattern, when updated, then value objects are replaced.</summary>
    [TestMethod]
    public void Given_DateRangeAndRecurrencePattern_When_Updated_Then_ValueObjectsAreReplaced()
    {
        var task = TaskItem.Create(TenantId, "Mutation sample").Value!;
        var startDate = DateTimeOffset.Parse("2026-05-27T12:00:00+00:00");
        var dueDate = DateTimeOffset.Parse("2026-06-03T12:00:00+00:00");
        var pattern = new RecurrencePattern
        {
            Frequency = "Weekly",
            Interval = 2,
            EndDate = dueDate
        };

        task.UpdateDateRange(startDate, dueDate);
        task.UpdateRecurrencePattern(pattern);

        Assert.AreEqual(startDate, task.DateRange.StartDate);
        Assert.AreEqual(dueDate, task.DateRange.DueDate);
        Assert.AreSame(pattern, task.RecurrencePattern);

        task.UpdateRecurrencePattern(null);

        Assert.IsNull(task.RecurrencePattern);
    }

    /// <summary>Creates task at status used by the surrounding test cases.</summary>
    private static TaskItem CreateTaskAtStatus(TaskItemStatus status)
    {
        var task = TaskItem.Create(TenantId, "Mutation sample").Value!;

        foreach (var nextStatus in PathTo(status))
        {
            Assert.IsTrue(task.TransitionStatus(nextStatus).IsSuccess);
        }

        Assert.AreEqual(status, task.Status);
        return task;
    }

    /// <summary>Verifies path to behavior and protects the expected test contract.</summary>
    private static TaskItemStatus[] PathTo(TaskItemStatus status) =>
        status switch
        {
            TaskItemStatus.Open => [],
            TaskItemStatus.InProgress => [TaskItemStatus.InProgress],
            TaskItemStatus.Blocked => [TaskItemStatus.InProgress, TaskItemStatus.Blocked],
            TaskItemStatus.Completed => [TaskItemStatus.InProgress, TaskItemStatus.Completed],
            TaskItemStatus.Cancelled => [TaskItemStatus.Cancelled],
            TaskItemStatus.None => [TaskItemStatus.None],
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
        };
}
