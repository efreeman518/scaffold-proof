using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Domain.Shared.Enums;
using Test.Support;

namespace Test.Unit.Domain;

/// <summary>
/// Validates the <see cref="TaskFlow.Domain.Model.Attachment"/> aggregate's factory and update rules:
/// required fields (file name, size, tenant), success/failure shape of <c>DomainResult</c>, and that
/// nullable update parameters preserve original values.
/// Pure-unit tier (no infra, no test host): the entity is a POCO - adding a DbContext or web factory would
/// not exercise additional behavior and would slow the feedback loop.
/// </summary>
[TestClass]
public class AttachmentTests
{
    private static TenantId TenantId => TenantId.From(TestConstants.TenantId);

    /// <summary>Verifies that given valid input, when attachment created, then returns success.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_ValidInput_When_AttachmentCreated_Then_ReturnsSuccess()
    {
        var ownerId = Guid.NewGuid();
        var result = Attachment.Create(TenantId, "file.pdf", "application/pdf", 1024, "https://storage/file.pdf", AttachmentOwnerType.TaskItem, ownerId);
        Assert.IsTrue(result.IsSuccess);
        Assert.IsNotNull(result.Value);
        Assert.AreEqual("file.pdf", result.Value.FileName);
        Assert.AreEqual(1024, result.Value.FileSizeBytes);
    }

    /// <summary>Verifies that given empty file name, when attachment created, then returns domain failure.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void Given_EmptyFileName_When_AttachmentCreated_Then_ReturnsDomainFailure(string? fileName)
    {
        var result = Attachment.Create(TenantId, fileName!, "application/pdf", 1024, "https://storage/file.pdf", AttachmentOwnerType.TaskItem, Guid.NewGuid());
        Assert.IsTrue(result.IsFailure);
    }

    /// <summary>Verifies that given zero file size, when attachment created, then returns domain failure.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_ZeroFileSize_When_AttachmentCreated_Then_ReturnsDomainFailure()
    {
        var result = Attachment.Create(TenantId, "file.pdf", "application/pdf", 0, "https://storage/file.pdf", AttachmentOwnerType.TaskItem, Guid.NewGuid());
        Assert.IsTrue(result.IsFailure);
    }

    /// <summary>Verifies that given empty tenant ID, when attachment created, then returns domain failure.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_EmptyTenantId_When_AttachmentCreated_Then_ReturnsDomainFailure()
    {
        var result = Attachment.Create(TenantId.From(Guid.Empty), "file.pdf", "application/pdf", 1024, "https://storage/file.pdf", AttachmentOwnerType.TaskItem, Guid.NewGuid());
        Assert.IsTrue(result.IsFailure);
    }

    /// <summary>Verifies that given existing attachment, when updated, then returns updated values.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_ExistingAttachment_When_Updated_Then_ReturnsUpdatedValues()
    {
        var attachment = Attachment.Create(TenantId, "old.pdf", "application/pdf", 1024, "https://storage/old.pdf", AttachmentOwnerType.TaskItem, Guid.NewGuid()).Value!;
        var result = attachment.Update(fileName: "new.pdf", fileSizeBytes: 2048);
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("new.pdf", result.Value!.FileName);
        Assert.AreEqual(2048, result.Value.FileSizeBytes);
    }

    /// <summary>Verifies that given null update, when updated, then original values preserved.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_NullUpdate_When_Updated_Then_OriginalValuesPreserved()
    {
        var attachment = Attachment.Create(TenantId, "file.pdf", "application/pdf", 1024, "https://storage/file.pdf", AttachmentOwnerType.TaskItem, Guid.NewGuid()).Value!;
        var result = attachment.Update();
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("file.pdf", result.Value!.FileName);
        Assert.AreEqual(1024, result.Value.FileSizeBytes);
    }
}
