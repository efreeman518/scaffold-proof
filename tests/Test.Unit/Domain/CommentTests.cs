using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using Test.Support;

namespace Test.Unit.Domain;

/// <summary>
/// Validates the <see cref="TaskFlow.Domain.Model.Comment"/> aggregate's factory and update rules:
/// required body and TaskItemId, and null-update preservation of the original body.
/// Pure-unit tier: POCO behavior only.
/// </summary>
[TestClass]
public class CommentTests
{
    private static TenantId TenantId => TenantId.From(TestConstants.TenantId);

    /// <summary>Verifies that given valid input, when comment created, then returns success.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_ValidInput_When_CommentCreated_Then_ReturnsSuccess()
    {
        var taskItemId = TaskItemId.From(Guid.NewGuid());
        var result = Comment.Create(TenantId, taskItemId, "Test comment body");
        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(result.IsSuccess, result.ToString());
        Assert.AreEqual("Test comment body", result.GetValueOrThrow().Body);
        Assert.AreEqual(taskItemId, result.GetValueOrThrow().TaskItemId);
    }

    /// <summary>Verifies that given empty body, when comment created, then returns domain failure.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void Given_EmptyBody_When_CommentCreated_Then_ReturnsDomainFailure(string? body)
    {
        var result = Comment.Create(TenantId, TaskItemId.From(Guid.NewGuid()), body!);
        Assert.IsFalse(result.IsSuccess);
    }

    /// <summary>Verifies that given empty task item ID, when comment created, then returns domain failure.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_EmptyTaskItemId_When_CommentCreated_Then_ReturnsDomainFailure()
    {
        var result = Comment.Create(TenantId, TaskItemId.From(Guid.Empty), "Body");
        Assert.IsFalse(result.IsSuccess);
    }

    /// <summary>Verifies that given existing comment, when updated, then returns updated values.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_ExistingComment_When_Updated_Then_ReturnsUpdatedValues()
    {
        var comment = Comment.Create(TenantId, TaskItemId.From(Guid.NewGuid()), "Original").GetValueOrThrow();
        var result = comment.Update(body: "Updated body");
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("Updated body", result.GetValueOrThrow().Body);
    }

    /// <summary>Verifies that given null update, when updated, then original values preserved.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_NullUpdate_When_Updated_Then_OriginalValuesPreserved()
    {
        var comment = Comment.Create(TenantId, TaskItemId.From(Guid.NewGuid()), "Original").GetValueOrThrow();
        var result = comment.Update();
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("Original", result.GetValueOrThrow().Body);
    }
}
