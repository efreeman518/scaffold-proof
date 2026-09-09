using EF.Common.Contracts;
using EF.Data.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.Models;
using TaskFlow.Application.Services;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Domain.Shared.Enums;
using Test.Support;
using Test.Support.Builders;

namespace Test.Unit.Services;

/// <summary>
/// Validates <see cref="TaskFlow.Application.Services.AttachmentService"/> orchestration with mocked
/// dependencies: CRUD success/failure paths plus search projection. Polymorphic owner-id behavior is
/// preserved via DTO shape but not persisted here.
/// Pure-unit tier (Moq only).
/// </summary>
[TestClass]
public class AttachmentServiceTests
{
    private readonly Mock<IAttachmentRepositoryTrxn> _repoTrxnMock = new();
    private readonly Mock<IAttachmentRepositoryQuery> _repoQueryMock = new();
    private readonly Mock<IRequestContext<string, Guid?>> _requestContextMock = new();
    private readonly Mock<ITenantBoundaryValidator> _tenantBoundaryValidatorMock = new();

    /// <summary>Prepares per-test fixtures so each test starts from a predictable state.</summary>
    [TestInitialize]
    public void Setup()
    {
        _requestContextMock.Setup(x => x.TenantId).Returns(TestConstants.TenantId);
        _requestContextMock.Setup(x => x.Roles).Returns(new List<string>());
        _tenantBoundaryValidatorMock
            .Setup(x => x.EnsureTenantBoundary(It.IsAny<ILogger>(), It.IsAny<Guid?>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid?>()))
            .Returns(Result.Success());
        _tenantBoundaryValidatorMock
            .Setup(x => x.PreventTenantChange(It.IsAny<ILogger>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(Result.Success());
    }

    /// <summary>Creates service used by the surrounding test cases.</summary>
    private AttachmentService CreateService() => new(
        NullLogger<AttachmentService>.Instance,
        _requestContextMock.Object,
        _repoTrxnMock.Object,
        _repoQueryMock.Object,
        _tenantBoundaryValidatorMock.Object);

    /// <summary>Verifies that given valid DTO, when create, then returns success.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Given_ValidDto_When_CreateAsync_Then_ReturnsSuccess()
    {
        _repoTrxnMock.Setup(r => r.Create(ref It.Ref<Attachment>.IsAny));
        _repoTrxnMock.Setup(r => r.SaveChangesAsync(It.IsAny<OptimisticConcurrencyWinner>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var dto = new AttachmentDto
        {
            FileName = "doc.pdf",
            ContentType = "application/pdf",
            FileSizeBytes = 1024,
            StorageUri = "https://storage.example.com/doc.pdf",
            OwnerType = AttachmentOwnerType.TaskItem,
            OwnerId = Guid.NewGuid()
        };
        var result = await CreateService().CreateAsync(new DefaultRequest<AttachmentDto> { Item = dto }, TestContext.CancellationToken);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("doc.pdf", result.Value!.Item!.FileName);
    }

    /// <summary>Verifies that given invalid DTO, when create, then returns failure.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Given_InvalidDto_When_CreateAsync_Then_ReturnsFailure()
    {
        var dto = new AttachmentDto { FileName = "", ContentType = "", FileSizeBytes = 0, StorageUri = "", OwnerId = Guid.Empty };
        var result = await CreateService().CreateAsync(new DefaultRequest<AttachmentDto> { Item = dto }, TestContext.CancellationToken);

        Assert.IsTrue(result.IsFailure);
    }

    /// <summary>
    /// Verifies the upload path enforces GR-17 before anything else - it used to skip UuidV7 validation
    /// entirely, so a caller-supplied Guid.Empty (or any v4) reached DomainId.FromNullable unchecked.
    /// blobStorage stays unconfigured (null) here specifically to prove the id check runs first: a stale
    /// fix that reordered the checks would surface as "Blob storage is not configured" instead.
    /// </summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Given_EmptyGuidCallerId_When_UploadAsync_Then_ReturnsFailureBeforeBlobStorageCheck()
    {
        using var stream = new MemoryStream();
        var result = await CreateService().UploadAsync(
            stream, "doc.pdf", "application/pdf", 0,
            AttachmentOwnerType.TaskItem, Guid.NewGuid(), id: Guid.Empty, ct: TestContext.CancellationToken);

        Assert.IsTrue(result.IsFailure);
        Assert.Contains("not a UUIDv7", result.ErrorMessage!, StringComparison.Ordinal);
    }

    /// <summary>Verifies that given existing entity, when get, then returns mapped DTO.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Given_ExistingEntity_When_GetAsync_Then_ReturnsMappedDto()
    {
        var entity = new AttachmentBuilder().Build();
        _repoQueryMock.Setup(r => r.GetAttachmentAsync(entity.Id, It.IsAny<CancellationToken>())).ReturnsAsync(entity);

        var result = await CreateService().GetAsync(entity.Id, TestContext.CancellationToken);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(entity.FileName, result.Value!.Item!.FileName);
    }

    /// <summary>Verifies that given non existent ID, when get, then returns none.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Given_NonExistentId_When_GetAsync_Then_ReturnsNone()
    {
        _repoQueryMock.Setup(r => r.GetAttachmentAsync(It.IsAny<AttachmentId>(), It.IsAny<CancellationToken>())).ReturnsAsync((Attachment?)null);

        var result = await CreateService().GetAsync(Guid.NewGuid(), TestContext.CancellationToken);

        Assert.IsTrue(result.IsNone);
    }

    /// <summary>Verifies that given existing entity, when update, then returns success.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Given_ExistingEntity_When_UpdateAsync_Then_ReturnsSuccess()
    {
        var entity = new AttachmentBuilder().Build();
        _repoTrxnMock.Setup(r => r.GetAttachmentAsync(entity.Id, It.IsAny<CancellationToken>())).ReturnsAsync(entity);
        _repoTrxnMock.Setup(r => r.SaveChangesAsync(It.IsAny<OptimisticConcurrencyWinner>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var dto = new AttachmentDto
        {
            Id = entity.Id,
            FileName = "updated.pdf",
            ContentType = "application/pdf",
            FileSizeBytes = 2048,
            StorageUri = "https://storage.example.com/updated.pdf",
            OwnerType = entity.OwnerType,
            OwnerId = entity.OwnerId
        };
        var result = await CreateService().UpdateAsync(new DefaultRequest<AttachmentDto> { Item = dto }, null, TestContext.CancellationToken);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("updated.pdf", result.Value!.Item!.FileName);
    }

    /// <summary>Verifies that given non existent ID, when update, then returns null item.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Given_NonExistentId_When_UpdateAsync_Then_ReturnsNullItem()
    {
        _repoTrxnMock.Setup(r => r.GetAttachmentAsync(It.IsAny<AttachmentId>(), It.IsAny<CancellationToken>())).ReturnsAsync((Attachment?)null);

        var dto = new AttachmentDto
        {
            Id = Guid.NewGuid(),
            FileName = "x.pdf",
            ContentType = "application/pdf",
            FileSizeBytes = 100,
            StorageUri = "https://storage.example.com/x.pdf",
            OwnerId = Guid.NewGuid()
        };
        var result = await CreateService().UpdateAsync(new DefaultRequest<AttachmentDto> { Item = dto }, null, TestContext.CancellationToken);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNull(result.Value?.Item);
    }

    /// <summary>Verifies that given existing entity, when delete, then returns success.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Given_ExistingEntity_When_DeleteAsync_Then_ReturnsSuccess()
    {
        var entity = new AttachmentBuilder().Build();
        _repoTrxnMock.Setup(r => r.GetAttachmentAsync(entity.Id, It.IsAny<CancellationToken>())).ReturnsAsync(entity);
        _repoTrxnMock.Setup(r => r.SaveChangesAsync(It.IsAny<OptimisticConcurrencyWinner>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var result = await CreateService().DeleteAsync(entity.Id, null, TestContext.CancellationToken);

        Assert.IsTrue(result.IsSuccess);
        _repoTrxnMock.Verify(r => r.Delete(entity), Times.Once);
    }

    /// <summary>Verifies that given non existent ID, when delete, then returns success idempotent.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Given_NonExistentId_When_DeleteAsync_Then_ReturnsSuccessIdempotent()
    {
        _repoTrxnMock.Setup(r => r.GetAttachmentAsync(It.IsAny<AttachmentId>(), It.IsAny<CancellationToken>())).ReturnsAsync((Attachment?)null);

        var result = await CreateService().DeleteAsync(Guid.NewGuid(), null, TestContext.CancellationToken);

        Assert.IsTrue(result.IsSuccess);
    }

    /// <summary>Verifies that given search request, when search, then returns paged response.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Given_SearchRequest_When_SearchAsync_Then_ReturnsPagedResponse()
    {
        var dtos = new List<AttachmentDto> { new() { FileName = "Test" } };
        var pagedResponse = new PagedResponse<AttachmentDto> { Data = dtos, Total = 1, PageSize = 10, PageIndex = 0 };
        _repoQueryMock.Setup(r => r.SearchAttachmentsAsync(It.IsAny<SearchRequest<AttachmentSearchFilter>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(pagedResponse);

        var request = new SearchRequest<AttachmentSearchFilter> { PageSize = 10, PageIndex = 0 };
        var response = await CreateService().SearchAsync(request, false, TestContext.CancellationToken);

        Assert.AreEqual(1, response.Total);
    }

    public TestContext TestContext { get; set; } = null!;
}
