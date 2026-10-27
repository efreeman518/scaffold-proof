using TaskFlow.Application.Mappers;
using TaskFlow.Application.Models;
using Test.Support;
using Test.Support.Builders;

namespace Test.Unit.Mappers;

/// <summary>
/// Validates <c>CommentMapper</c> entity <-> DTO mapping: body and parent <c>TaskItemId</c> preservation
/// plus failure surfacing for invalid DTO input.
/// Pure-unit tier: static mapping extensions only.
/// </summary>
[TestClass]
public class CommentMapperTests
{
    /// <summary>Verifies that given valid entity, when mapped to DTO, then all properties mapped.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_ValidEntity_When_MappedToDto_Then_AllPropertiesMapped()
    {
        var taskItemId = Guid.NewGuid();
        var entity = new CommentBuilder().WithTaskItemId(taskItemId).WithBody("Test body").Build();
        var dto = entity.ToDto();

        Assert.AreEqual(entity.Id, dto.Id);
        Assert.AreEqual(entity.Body, dto.Body);
        Assert.AreEqual(entity.TaskItemId, dto.TaskItemId);
    }

    /// <summary>Verifies that given valid DTO, when mapped to entity, then returns success domain result.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_ValidDto_When_MappedToEntity_Then_ReturnsSuccessResult()
    {
        var taskItemId = Guid.NewGuid();
        var dto = new CommentDto { Body = "A comment", TaskItemId = taskItemId };
        var result = dto.ToEntity(TestConstants.TenantId);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("A comment", result.GetValueOrThrow().Body);
        Assert.AreEqual(taskItemId, result.GetValueOrThrow().TaskItemId);
    }

    /// <summary>Verifies that given invalid DTO, when mapped to entity, then returns failure.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_InvalidDto_When_MappedToEntity_Then_ReturnsFailure()
    {
        var dto = new CommentDto { Body = "", TaskItemId = Guid.Empty };
        var result = dto.ToEntity(TestConstants.TenantId);

        Assert.IsFalse(result.IsSuccess);
    }
}
