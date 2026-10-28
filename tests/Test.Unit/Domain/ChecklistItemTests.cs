using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using Test.Support;

namespace Test.Unit.Domain;

/// <summary>
/// Validates the <see cref="TaskFlow.Domain.Model.ChecklistItem"/> aggregate's factory and update rules,
/// including the <c>IsCompleted</c> <-> <c>CompletedDate</c> derived-state invariant when toggling completion.
/// Pure-unit tier: factory and method calls only - no persistence, no projection.
/// </summary>
[TestClass]
public class ChecklistItemTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private static TenantId TenantId => TenantId.From(TestConstants.TenantId);

    /// <summary>Verifies that given valid input, when checklist item created, then returns success.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_ValidInput_When_ChecklistItemCreated_Then_ReturnsSuccess()
    {
        var taskItemId = TaskItemId.From(Guid.NewGuid());
        var result = ChecklistItem.Create(TenantId, taskItemId, "Test Item", 1);
        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(result.IsSuccess, result.ToString());
        Assert.AreEqual("Test Item", result.GetValueOrThrow().Title);
        Assert.IsFalse(result.GetValueOrThrow().IsCompleted);
        Assert.IsNull(result.GetValueOrThrow().CompletedDate);
    }

    /// <summary>Verifies that given empty title, when checklist item created, then returns domain failure.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void Given_EmptyTitle_When_ChecklistItemCreated_Then_ReturnsDomainFailure(string? title)
    {
        var result = ChecklistItem.Create(TenantId, TaskItemId.From(Guid.NewGuid()), title!);
        Assert.IsFalse(result.IsSuccess);
    }

    /// <summary>Verifies that given empty task item ID, when checklist item created, then returns domain failure.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_EmptyTaskItemId_When_ChecklistItemCreated_Then_ReturnsDomainFailure()
    {
        var result = ChecklistItem.Create(TenantId, TaskItemId.From(Guid.Empty), "Title");
        Assert.IsFalse(result.IsSuccess);
    }

    /// <summary>Verifies that given existing item, when updated with title, then returns updated values.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_ExistingItem_When_UpdatedWithTitle_Then_ReturnsUpdatedValues()
    {
        var item = ChecklistItem.Create(TenantId, TaskItemId.From(Guid.NewGuid()), "Original").GetValueOrThrow();
        var result = item.Update(Now, title: "Updated");
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("Updated", result.GetValueOrThrow().Title);
    }

    /// <summary>Verifies that given incomplete item, when completion set, then completed date set.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_IncompleteItem_When_CompletionSet_Then_CompletedDateSet()
    {
        var item = ChecklistItem.Create(TenantId, TaskItemId.From(Guid.NewGuid()), "Item").GetValueOrThrow();
        var result = item.Update(Now, isCompleted: true);
        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(result.GetValueOrThrow().IsCompleted);
        Assert.AreEqual(Now, result.GetValueOrThrow().CompletedDate, "The completion stamp is the caller's clock reading.");
    }

    /// <summary>Verifies that given completed item, when uncompleted, then completed date cleared.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_CompletedItem_When_Uncompleted_Then_CompletedDateCleared()
    {
        var item = ChecklistItem.Create(TenantId, TaskItemId.From(Guid.NewGuid()), "Item").GetValueOrThrow();
        item.Update(Now, isCompleted: true);
        Assert.IsNotNull(item.CompletedDate);

        var result = item.Update(Now, isCompleted: false);
        Assert.IsTrue(result.IsSuccess);
        Assert.IsFalse(result.GetValueOrThrow().IsCompleted);
        Assert.IsNull(result.GetValueOrThrow().CompletedDate);
    }

    /// <summary>Verifies that given null update, when updated, then original values preserved.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_NullUpdate_When_Updated_Then_OriginalValuesPreserved()
    {
        var item = ChecklistItem.Create(TenantId, TaskItemId.From(Guid.NewGuid()), "Original", 5).GetValueOrThrow();
        var result = item.Update(Now);
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("Original", result.GetValueOrThrow().Title);
        Assert.AreEqual(5, result.GetValueOrThrow().SortOrder);
    }
}
