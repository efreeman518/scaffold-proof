using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using Test.Support;

namespace Test.Unit.Domain;

/// <summary>
/// Validates the <see cref="TaskFlow.Domain.Model.TaskItemTag"/> bridge entity's factory rules - both
/// foreign-key sides (TaskItemId, TagId) and TenantId must be non-empty.
/// Pure-unit tier: factory inspection only; the many-to-many join is exercised via real SQL in
/// <c>MigrationAndRepositoryTests</c>.
/// </summary>
[TestClass]
public class TaskItemTagTests
{
    private static TenantId TenantId => TenantId.From(TestConstants.TenantId);

    /// <summary>Verifies that given valid input, when task item tag created, then returns success.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_ValidInput_When_TaskItemTagCreated_Then_ReturnsSuccess()
    {
        var taskItemId = TaskItemId.From(Guid.NewGuid());
        var tagId = TagId.From(Guid.NewGuid());
        var result = TaskItemTag.Create(TenantId, taskItemId, tagId);
        Assert.IsTrue(result.IsSuccess);
        Assert.IsNotNull(result.Value);
        Assert.AreEqual(taskItemId, result.Value.TaskItemId);
        Assert.AreEqual(tagId, result.Value.TagId);
    }

    /// <summary>Verifies that given empty task item ID, when task item tag created, then returns domain failure.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_EmptyTaskItemId_When_TaskItemTagCreated_Then_ReturnsDomainFailure()
    {
        var result = TaskItemTag.Create(TenantId, TaskItemId.From(Guid.Empty), TagId.From(Guid.NewGuid()));
        Assert.IsTrue(result.IsFailure);
    }

    /// <summary>Verifies that given empty tag ID, when task item tag created, then returns domain failure.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_EmptyTagId_When_TaskItemTagCreated_Then_ReturnsDomainFailure()
    {
        var result = TaskItemTag.Create(TenantId, TaskItemId.From(Guid.NewGuid()), TagId.From(Guid.Empty));
        Assert.IsTrue(result.IsFailure);
    }

    /// <summary>Verifies that given empty tenant ID, when task item tag created, then returns domain failure.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_EmptyTenantId_When_TaskItemTagCreated_Then_ReturnsDomainFailure()
    {
        var result = TaskItemTag.Create(
            TenantId.From(Guid.Empty),
            TaskItemId.From(Guid.NewGuid()),
            TagId.From(Guid.NewGuid()));
        Assert.IsTrue(result.IsFailure);
    }
}
