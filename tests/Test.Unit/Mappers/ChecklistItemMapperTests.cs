using TaskFlow.Application.Mappers;
using TaskFlow.Application.Models;
using Test.Support;
using Test.Support.Builders;

namespace Test.Unit.Mappers;

/// <summary>
/// Validates <c>ChecklistItemMapper</c> entity <-> DTO mapping including the parent <c>TaskItemId</c>
/// linkage and failure surfacing for invalid DTO input.
/// Pure-unit tier: static mapping extensions only.
/// </summary>
[TestClass]
public class ChecklistItemMapperTests
{
    /// <summary>Verifies that given valid entity, when mapped to DTO, then all properties mapped.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_ValidEntity_When_MappedToDto_Then_AllPropertiesMapped()
    {
        var taskItemId = Guid.NewGuid();
        var entity = new ChecklistItemBuilder().WithTaskItemId(taskItemId).WithSortOrder(2).Build();
        var dto = entity.ToDto();

        Assert.AreEqual(entity.Id, dto.Id);
        Assert.AreEqual(entity.Title, dto.Title);
        Assert.AreEqual(entity.IsCompleted, dto.IsCompleted);
        Assert.AreEqual(entity.SortOrder, dto.SortOrder);
        Assert.AreEqual(entity.CompletedDate, dto.CompletedDate);
        Assert.AreEqual(entity.TaskItemId, dto.TaskItemId);
    }

    /// <summary>Verifies that given valid DTO, when mapped to entity, then returns success domain result.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_ValidDto_When_MappedToEntity_Then_ReturnsSuccessResult()
    {
        var taskItemId = Guid.NewGuid();
        var dto = new ChecklistItemDto { Title = "Step 1", TaskItemId = taskItemId, SortOrder = 1 };
        var result = dto.ToEntity(TestConstants.TenantId);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("Step 1", result.GetValueOrThrow().Title);
        Assert.AreEqual(taskItemId, result.GetValueOrThrow().TaskItemId);
        Assert.AreEqual(1, result.GetValueOrThrow().SortOrder);
    }

    /// <summary>Verifies that given invalid DTO, when mapped to entity, then returns failure.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_InvalidDto_When_MappedToEntity_Then_ReturnsFailure()
    {
        var dto = new ChecklistItemDto { Title = "", TaskItemId = Guid.Empty };
        var result = dto.ToEntity(TestConstants.TenantId);

        Assert.IsFalse(result.IsSuccess);
    }
}
