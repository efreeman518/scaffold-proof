using EF.Domain.Contracts;
using TaskFlow.Application.Mappers;
using TaskFlow.Application.Models;
using Test.Support;
using Test.Support.Builders;

namespace Test.Unit.Mappers;

/// <summary>
/// Validates <c>CategoryMapper</c> entity <-> DTO round-trips: every property is preserved,
/// <c>ParentCategoryId</c> survives mapping, and invalid DTO input is surfaced as a <c>Result</c>
/// failure rather than throwing.
/// Pure-unit tier: static extension methods over POCOs - no DbContext, no DI.
/// </summary>
[TestClass]
public class CategoryMapperTests
{
    /// <summary>Verifies that given valid entity, when mapped to DTO, then all properties mapped.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_ValidEntity_When_MappedToDto_Then_AllPropertiesMapped()
    {
        var entity = new CategoryBuilder().WithSortOrder(3).Build();
        var dto = entity.ToDto();

        Assert.AreEqual(entity.Id.Value, dto.Id);
        Assert.AreEqual(entity.Name, dto.Name);
        Assert.AreEqual(entity.Description, dto.Description);
        Assert.AreEqual(entity.SortOrder, dto.SortOrder);
        Assert.AreEqual(entity.IsActive, dto.IsActive);
        Assert.AreEqual(entity.ParentCategoryId?.Value, dto.ParentCategoryId);
    }

    /// <summary>Verifies that given valid DTO, when mapped to entity, then returns success domain result.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_ValidDto_When_MappedToEntity_Then_ReturnsSuccessResult()
    {
        var dto = new CategoryDto { Name = "From DTO", Description = "Test desc", SortOrder = 2 };
        var result = dto.ToEntity(TestConstants.TenantId);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("From DTO", result.GetValueOrThrow().Name);
        Assert.AreEqual("Test desc", result.GetValueOrThrow().Description);
        Assert.AreEqual(2, result.GetValueOrThrow().SortOrder);
    }

    /// <summary>Verifies that given DTO with parent category ID, when mapped to entity, then parent category ID preserved.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_DtoWithParentCategoryId_When_MappedToEntity_Then_ParentCategoryIdPreserved()
    {
        var parentId = Guid.NewGuid();
        var dto = new CategoryDto { Name = "Child", ParentCategoryId = parentId };
        var result = dto.ToEntity(TestConstants.TenantId);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(parentId, result.GetValueOrThrow().ParentCategoryId!.Value);
    }

    /// <summary>Verifies that given invalid DTO, when mapped to entity, then returns failure.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_InvalidDto_When_MappedToEntity_Then_ReturnsFailure()
    {
        var dto = new CategoryDto { Name = "" };
        var result = dto.ToEntity(TestConstants.TenantId);

        Assert.IsFalse(result.IsSuccess);
    }
}
