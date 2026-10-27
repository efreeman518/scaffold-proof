using TaskFlow.Application.Mappers;
using TaskFlow.Application.Models;
using Test.Support;
using Test.Support.Builders;

namespace Test.Unit.Mappers;

/// <summary>
/// Validates <c>TaskItemTagMapper</c> entity <-> DTO mapping for the bridge type and failure surfacing
/// for empty foreign keys.
/// Pure-unit tier: static mapping extensions only.
/// </summary>
[TestClass]
public class TaskItemTagMapperTests
{
    /// <summary>Verifies that given valid entity, when mapped to DTO, then all properties mapped.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_ValidEntity_When_MappedToDto_Then_AllPropertiesMapped()
    {
        var taskItemId = Guid.NewGuid();
        var tagId = Guid.NewGuid();
        var entity = new TaskItemTagBuilder().WithTaskItemId(taskItemId).WithTagId(tagId).Build();
        var dto = entity.ToDto();

        Assert.AreEqual(entity.Id, dto.Id);
        Assert.AreEqual(entity.TaskItemId, dto.TaskItemId);
        Assert.AreEqual(entity.TagId, dto.TagId);
    }

    /// <summary>Verifies that given valid DTO, when mapped to entity, then returns success domain result.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_ValidDto_When_MappedToEntity_Then_ReturnsSuccessResult()
    {
        var taskItemId = Guid.NewGuid();
        var tagId = Guid.NewGuid();
        var dto = new TaskItemTagDto { TaskItemId = taskItemId, TagId = tagId };
        var result = dto.ToEntity(TestConstants.TenantId);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(taskItemId, result.GetValueOrThrow().TaskItemId);
        Assert.AreEqual(tagId, result.GetValueOrThrow().TagId);
    }

    /// <summary>Verifies that given invalid DTO, when mapped to entity, then returns failure.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_InvalidDto_When_MappedToEntity_Then_ReturnsFailure()
    {
        var dto = new TaskItemTagDto { TaskItemId = Guid.Empty, TagId = Guid.Empty };
        var result = dto.ToEntity(TestConstants.TenantId);

        Assert.IsFalse(result.IsSuccess);
    }
}
