using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using Test.Support;

namespace Test.Unit.Domain;

/// <summary>
/// Validates the <see cref="TaskFlow.Domain.Model.Tag"/> aggregate's factory and update rules including
/// color preservation and tenant-id guards.
/// Pure-unit tier: POCO behavior only.
/// </summary>
[TestClass]
public class TagTests
{
    private static TenantId TenantId => TenantId.From(TestConstants.TenantId);

    /// <summary>Verifies that given valid input, when tag created, then returns success.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_ValidInput_When_TagCreated_Then_ReturnsSuccess()
    {
        var result = Tag.Create(TenantId, "Test Tag", "#FF0000");
        Assert.IsTrue(result.IsSuccess);
        Assert.IsNotNull(result.Value);
        Assert.AreEqual("Test Tag", result.Value.Name);
        Assert.AreEqual("#FF0000", result.Value.Color);
    }

    /// <summary>Verifies that given empty name, when tag created, then returns domain failure.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void Given_EmptyName_When_TagCreated_Then_ReturnsDomainFailure(string? name)
    {
        var result = Tag.Create(TenantId, name!);
        Assert.IsTrue(result.IsFailure);
    }

    /// <summary>Verifies that given empty tenant ID, when tag created, then returns domain failure.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_EmptyTenantId_When_TagCreated_Then_ReturnsDomainFailure()
    {
        var result = Tag.Create(TenantId.From(Guid.Empty), "Test");
        Assert.IsTrue(result.IsFailure);
    }

    /// <summary>Verifies that given existing tag, when updated, then returns updated values.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_ExistingTag_When_Updated_Then_ReturnsUpdatedValues()
    {
        var tag = Tag.Create(TenantId, "Original", "#000000").Value!;
        var result = tag.Update(name: "Updated", color: "#FFFFFF");
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("Updated", result.Value!.Name);
        Assert.AreEqual("#FFFFFF", result.Value.Color);
    }

    /// <summary>Verifies that given null update, when updated, then original values preserved.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_NullUpdate_When_Updated_Then_OriginalValuesPreserved()
    {
        var tag = Tag.Create(TenantId, "Original", "#000000").Value!;
        var result = tag.Update();
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("Original", result.Value!.Name);
        Assert.AreEqual("#000000", result.Value.Color);
    }
}
