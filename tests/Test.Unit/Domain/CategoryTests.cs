using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using Test.Support;

namespace Test.Unit.Domain;

/// <summary>
/// Validates the <see cref="TaskFlow.Domain.Model.Category"/> aggregate's factory and update rules,
/// including parent-category wiring and tenant-id guards.
/// Pure-unit tier: instantiates the entity directly - no EF, no DI, no test host.
/// </summary>
[TestClass]
public class CategoryTests
{
    private static TenantId TenantId => TenantId.From(TestConstants.TenantId);

    /// <summary>Verifies that given valid input, when category created, then returns success.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_ValidInput_When_CategoryCreated_Then_ReturnsSuccess()
    {
        var result = Category.Create(TenantId, "Test Category");
        Assert.IsTrue(result.IsSuccess);
        Assert.IsNotNull(result.Value);
        Assert.AreEqual("Test Category", result.Value.Name);
        Assert.IsTrue(result.Value.IsActive);
    }

    /// <summary>Verifies that given empty name, when category created, then returns domain failure.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void Given_EmptyName_When_CategoryCreated_Then_ReturnsDomainFailure(string? name)
    {
        var result = Category.Create(TenantId, name!);
        Assert.IsTrue(result.IsFailure);
    }

    /// <summary>Verifies that given empty tenant ID, when category created, then returns domain failure.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_EmptyTenantId_When_CategoryCreated_Then_ReturnsDomainFailure()
    {
        var result = Category.Create(TenantId.From(Guid.Empty), "Test");
        Assert.IsTrue(result.IsFailure);
    }

    /// <summary>Verifies that given existing category, when updated, then returns updated values.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_ExistingCategory_When_Updated_Then_ReturnsUpdatedValues()
    {
        var cat = Category.Create(TenantId, "Original").Value!;
        var result = cat.Update(name: "Updated", description: "New desc");
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("Updated", result.Value!.Name);
        Assert.AreEqual("New desc", result.Value.Description);
    }

    /// <summary>Verifies that given null update, when updated, then original values preserved.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_NullUpdate_When_Updated_Then_OriginalValuesPreserved()
    {
        var cat = Category.Create(TenantId, "Original", "Desc").Value!;
        var result = cat.Update();
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("Original", result.Value!.Name);
        Assert.AreEqual("Desc", result.Value.Description);
    }

    /// <summary>Verifies that given category with parent, when created, then parent ID set.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_CategoryWithParent_When_Created_Then_ParentIdSet()
    {
        var parentId = CategoryId.From(Guid.NewGuid());
        var result = Category.Create(TenantId, "Child", parentCategoryId: parentId);
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(parentId, result.Value!.ParentCategoryId!.Value);
    }
}
