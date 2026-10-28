using TaskFlow.Application.Mappers;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using EF.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using Test.Support;

namespace Test.Unit.Mappers;

/// <summary>
/// Verifies every mapper projects the aggregate <c>Version</c> (D-021). The version is the ETag: a
/// mapper that silently drops it produces DTOs whose clients can never satisfy an If-Match, so the
/// whole concurrency contract fails open at that one entity.
/// Pure-unit tier: entities are saved through an in-memory context purely so the version interceptor
/// assigns a real value, then mapped.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class MapperVersionProjectionTests
{
    /// <summary>Builds a throwaway in-memory write context stamped by the EF.Data save pipeline.</summary>
    private static TaskFlow.Infrastructure.Data.TaskFlowDbContextTrxn CreateContext() =>
        new(new DbContextOptionsBuilder<TaskFlow.Infrastructure.Data.TaskFlowDbContextTrxn>()
            .UseInMemoryDatabase($"MapperVersion_{Guid.NewGuid()}")
            .Options)
        {
            AuditId = "mapper-version-test",
            TenantId = TestConstants.TenantId
        };

    /// <summary>Verifies all seven mappers carry the persisted version into their DTO.</summary>
    [TestMethod]
    public async Task Given_PersistedEntities_When_Mapped_Then_AllMappersProjectVersion()
    {
        await using var db = CreateContext();
        var tenantId = TenantId.From(TestConstants.TenantId);

        var category = Category.Create(tenantId, "Ops").GetValueOrThrow();
        var tag = Tag.Create(tenantId, "urgent").GetValueOrThrow();
        var taskItem = TaskItem.Create(tenantId, "Versioned").GetValueOrThrow();
        var comment = taskItem.AddComment("body").GetValueOrThrow();
        var checklistItem = taskItem.AddChecklistItem("step").GetValueOrThrow();
        var association = taskItem.AssociateTag(tag.Id).GetValueOrThrow();
        var attachment = Attachment.Create(
            tenantId, "file.txt", "text/plain", 12, "https://example/blob",
            TaskFlow.Domain.Shared.Enums.AttachmentOwnerType.TaskItem, taskItem.Id.Value).GetValueOrThrow();

        db.AddRange(category, tag, taskItem, attachment);
        await db.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(category.Version, category.ToDto().Version);
        Assert.AreEqual(tag.Version, tag.ToDto().Version);
        Assert.AreEqual(taskItem.Version, taskItem.ToDto().Version);
        Assert.AreEqual(comment.Version, comment.ToDto().Version);
        Assert.AreEqual(checklistItem.Version, checklistItem.ToDto().Version);
        Assert.AreEqual(association.Version, association.ToDto().Version);
        Assert.AreEqual(attachment.Version, attachment.ToDto().Version);

        // A version of 0 would make every assertion above trivially true.
        Assert.IsGreaterThan(0, taskItem.Version);
    }

    /// <summary>Verifies the lean search projection carries Version and ModifiedAtUtc, the ModifiedDesc sort key.</summary>
    [TestMethod]
    public async Task Given_PersistedTaskItem_When_SearchProjected_Then_CarriesVersionAndModifiedAt()
    {
        await using var db = CreateContext();
        var taskItem = TaskItem.Create(TenantId.From(TestConstants.TenantId), "Search shape").GetValueOrThrow();
        db.Add(taskItem);
        await db.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, cancellationToken: TestContext.CancellationToken);

        var projected = TaskItemMapper.ProjectorSearch.Compile()(taskItem);

        Assert.AreEqual(taskItem.Version, projected.Version);
        Assert.AreEqual(taskItem.ModifiedAtUtc, projected.ModifiedAtUtc);
    }

    /// <summary>Verifies a caller-supplied v7 id survives the DTO-to-entity mapping (AR-01).</summary>
    [TestMethod]
    public void Given_CallerSuppliedId_When_MappedToEntity_Then_IdIsHonored()
    {
        var id = Guid.CreateVersion7();

        var taskItem = new TaskItemDto { Id = id, Title = "Caller id" }.ToEntity(TestConstants.TenantId, DateTimeOffset.UtcNow).GetValueOrThrow();
        var category = new CategoryDto { Id = id, Name = "Caller id" }.ToEntity(TestConstants.TenantId).GetValueOrThrow();
        var tag = new TagDto { Id = id, Name = "caller-id" }.ToEntity(TestConstants.TenantId).GetValueOrThrow();

        Assert.AreEqual(id, taskItem.Id.Value);
        Assert.AreEqual(id, category.Id.Value);
        Assert.AreEqual(id, tag.Id.Value);
    }

    public TestContext TestContext { get; set; } = null!;
}
