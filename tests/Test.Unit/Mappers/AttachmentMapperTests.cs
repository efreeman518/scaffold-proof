using TaskFlow.Application.Mappers;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Shared.Enums;
using Test.Support;
using Test.Support.Builders;

namespace Test.Unit.Mappers;

/// <summary>
/// Validates <c>AttachmentMapper</c> entity <-> DTO mapping including polymorphic owner fields
/// (OwnerType, OwnerId) and failure surfacing for invalid DTO input.
/// Pure-unit tier: static mapping extensions only.
/// </summary>
[TestClass]
public class AttachmentMapperTests
{
    /// <summary>Verifies that given valid entity, when mapped to DTO, then all properties mapped.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_ValidEntity_When_MappedToDto_Then_AllPropertiesMapped()
    {
        var ownerId = Guid.NewGuid();
        var entity = new AttachmentBuilder().WithOwnerId(ownerId).WithOwnerType(AttachmentOwnerType.TaskItem).Build();
        var dto = entity.ToDto();

        Assert.AreEqual(entity.Id, dto.Id);
        Assert.AreEqual(entity.FileName, dto.FileName);
        Assert.AreEqual(entity.ContentType, dto.ContentType);
        Assert.AreEqual(entity.FileSizeBytes, dto.FileSizeBytes);
        Assert.AreEqual(entity.StorageUri, dto.StorageUri);
        Assert.AreEqual(entity.OwnerType, dto.OwnerType);
        Assert.AreEqual(entity.OwnerId, dto.OwnerId);
    }

    /// <summary>Verifies that given valid DTO, when mapped to entity, then returns success domain result.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_ValidDto_When_MappedToEntity_Then_ReturnsSuccessResult()
    {
        var ownerId = Guid.NewGuid();
        var dto = new AttachmentDto
        {
            FileName = "report.pdf",
            ContentType = "application/pdf",
            FileSizeBytes = 2048,
            StorageUri = "https://storage.example.com/report.pdf",
            OwnerType = AttachmentOwnerType.TaskItem,
            OwnerId = ownerId
        };
        var result = dto.ToEntity(TestConstants.TenantId);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("report.pdf", result.GetValueOrThrow().FileName);
        Assert.AreEqual(ownerId, result.GetValueOrThrow().OwnerId);
    }

    /// <summary>Verifies that given invalid DTO, when mapped to entity, then returns failure.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_InvalidDto_When_MappedToEntity_Then_ReturnsFailure()
    {
        var dto = new AttachmentDto { FileName = "", ContentType = "", FileSizeBytes = 0, StorageUri = "", OwnerId = Guid.Empty };
        var result = dto.ToEntity(TestConstants.TenantId);

        Assert.IsFalse(result.IsSuccess);
    }
}
