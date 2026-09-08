using System.Text.Json;
using TaskFlow.Application.Models;
using TaskFlow.Application.Models.Reads;
using TaskFlow.Contracts.Grpc;
// The generated messages reuse the domain enum names (TaskItemStatus, Priority, AttachmentOwnerType), so
// the domain side is aliased rather than imported: every bare enum reference below is the DTO's.
using AttachmentOwnerType = TaskFlow.Domain.Shared.Enums.AttachmentOwnerType;
using Priority = TaskFlow.Domain.Shared.Enums.Priority;
using TaskFeatures = TaskFlow.Domain.Shared.Enums.TaskFeatures;
using TaskItemStatus = TaskFlow.Domain.Shared.Enums.TaskItemStatus;

namespace Test.Unit.Contracts;

/// <summary>
/// D-054: pins the DTO to protobuf mapping. The gRPC read service and the REST endpoints must answer the
/// same question with the same data, and this mapper is the only place the two representations meet - a
/// dropped field here is a field the dashboard silently loses when the transport switches.
///
/// The three documented lossy conversions are asserted as behavior rather than avoided: an offset is
/// normalized to UTC, a decimal survives as an invariant string, and a null collection comes back empty.
/// Pure-unit tier: no host, no container.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class TaskFlowReadGrpcMapperTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset GeneratedAt = new(2026, 9, 8, 14, 30, 15, TimeSpan.Zero);

    /// <summary>Every summary field survives the round trip, including the per-status buckets.</summary>
    [TestMethod]
    public void Given_Summary_When_RoundTripped_Then_EveryFieldSurvives()
    {
        var dto = new TaskItemSummaryDto
        {
            ByStatus =
            [
                new TaskItemStatusCountDto(TaskItemStatus.Open, 7),
                new TaskItemStatusCountDto(TaskItemStatus.Completed, 3),
                new TaskItemStatusCountDto(TaskItemStatus.Cancelled, 1)
            ],
            Overdue = 2,
            Total = 11,
            GeneratedAtUtc = GeneratedAt
        };

        var actual = dto.ToProto().ToDto();

        Assert.AreEqual(dto.Overdue, actual.Overdue);
        Assert.AreEqual(dto.Total, actual.Total);
        Assert.AreEqual(dto.GeneratedAtUtc, actual.GeneratedAtUtc);
        CollectionAssert.AreEqual(dto.ByStatus.ToArray(), actual.ByStatus.ToArray());
    }

    /// <summary>Category and tag lists survive with their optional fields present and absent.</summary>
    [TestMethod]
    public void Given_Metadata_When_RoundTripped_Then_ListsAndOptionalFieldsSurvive()
    {
        var parentId = Guid.NewGuid();
        var dto = new TaskMetadataDto
        {
            Categories =
            [
                new CategoryDto
                {
                    Id = Guid.NewGuid(), Version = 4, TenantId = TenantId, Name = "Ops",
                    Description = "Operational work", SortOrder = 2, IsActive = true, ParentCategoryId = parentId
                },
                // Second category exercises the absent arm of every optional field.
                new CategoryDto { Id = Guid.NewGuid(), TenantId = TenantId, Name = "Ad hoc" }
            ],
            Tags =
            [
                new TagDto { Id = Guid.NewGuid(), Version = 9, TenantId = TenantId, Name = "urgent", Color = "#ff0000" },
                new TagDto { Id = Guid.NewGuid(), TenantId = TenantId, Name = "later" }
            ],
            GeneratedAtUtc = GeneratedAt
        };

        var actual = dto.ToProto().ToDto();

        Assert.AreEqual(dto.GeneratedAtUtc, actual.GeneratedAtUtc);
        CollectionAssert.AreEqual(dto.Categories.ToArray(), actual.Categories.ToArray());
        CollectionAssert.AreEqual(dto.Tags.ToArray(), actual.Tags.ToArray());
    }

    /// <summary>A fully populated task item, children included, survives the round trip.</summary>
    [TestMethod]
    public void Given_TaskItemWithChildren_When_RoundTripped_Then_EveryFieldSurvives()
    {
        var taskId = Guid.NewGuid();
        var dto = new TaskItemDto
        {
            Id = taskId,
            Version = 12,
            TenantId = TenantId,
            Title = "Ship the gRPC read service",
            Description = "Internal only",
            Priority = Priority.High,
            Status = TaskItemStatus.InProgress,
            // A [Flags] combination: the field is a raw bitmask precisely so this survives.
            Features = TaskFeatures.Recurring | TaskFeatures.Reminder,
            EstimatedEffort = 12.50m,
            ActualEffort = 3.125m,
            CompletedDate = null,
            ModifiedAtUtc = GeneratedAt,
            CategoryId = Guid.NewGuid(),
            ParentTaskItemId = null,
            StartDate = GeneratedAt.AddDays(-1),
            DueDate = GeneratedAt.AddDays(5),
            RecurrenceInterval = 2,
            RecurrenceFrequency = "Weekly",
            RecurrenceEndDate = GeneratedAt.AddMonths(3),
            CategoryName = "Ops",
            Comments =
            [
                new CommentDto
                {
                    Id = Guid.NewGuid(), TenantId = TenantId, Body = "Looks good", TaskItemId = taskId,
                    Attachments =
                    [
                        new AttachmentDto
                        {
                            Id = Guid.NewGuid(), TenantId = TenantId, FileName = "notes.md",
                            ContentType = "text/markdown", FileSizeBytes = 2048, StorageUri = "s3://bucket/notes.md",
                            OwnerType = AttachmentOwnerType.Comment, OwnerId = taskId
                        }
                    ]
                }
            ],
            ChecklistItems =
            [
                new ChecklistItemDto
                {
                    Id = Guid.NewGuid(), TenantId = TenantId, Title = "Write the proto", IsCompleted = true,
                    SortOrder = 1, CompletedDate = GeneratedAt, TaskItemId = taskId
                }
            ],
            Tags = [new TagDto { Id = Guid.NewGuid(), TenantId = TenantId, Name = "grpc" }],
            Attachments = [],
            SubTasks =
            [
                new TaskItemDto { Id = Guid.NewGuid(), TenantId = TenantId, Title = "Subtask", Attachments = [], Comments = [], ChecklistItems = [], Tags = [], SubTasks = [] }
            ]
        };

        var actual = dto.ToProto().ToDto();

        // Compared as JSON, not with Assert.AreEqual: these records carry List<T> members, and the
        // compiler-synthesized record equality compares those by reference, so AreEqual would pass on a
        // mapper that dropped every child collection.
        Assert.AreEqual(JsonSerializer.Serialize(dto), JsonSerializer.Serialize(actual));
    }

    /// <summary>
    /// The documented normalizations, stated as assertions so a change to any of them is a failing test
    /// rather than a surprise: the offset collapses to UTC, the decimal keeps its scale, and null
    /// collections come back as empty ones.
    /// </summary>
    [TestMethod]
    public void Given_LossyShapes_When_RoundTripped_Then_TheDocumentedNormalizationsApply()
    {
        var dto = new TaskItemDto
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            Title = "Normalization",
            // Deliberately not UTC: Timestamp is an instant, so the offset does not survive.
            DueDate = new DateTimeOffset(2026, 9, 8, 9, 0, 0, TimeSpan.FromHours(-5)),
            EstimatedEffort = 0.001m,
            Comments = null,
            ChecklistItems = null,
            Tags = null,
            Attachments = null,
            SubTasks = null
        };

        var actual = dto.ToProto().ToDto();

        Assert.AreEqual(dto.DueDate!.Value.ToUniversalTime(), actual.DueDate!.Value);
        Assert.AreEqual(TimeSpan.Zero, actual.DueDate!.Value.Offset);
        Assert.AreEqual(0.001m, actual.EstimatedEffort);

        Assert.IsNotNull(actual.Comments);
        Assert.AreEqual(0, actual.Comments!.Count);
        Assert.AreEqual(0, actual.ChecklistItems!.Count);
        Assert.AreEqual(0, actual.Tags!.Count);
        Assert.AreEqual(0, actual.Attachments!.Count);
        Assert.AreEqual(0, actual.SubTasks!.Count);
    }

    /// <summary>An id that was never assigned maps to an empty string and back to null, not Guid.Empty.</summary>
    [TestMethod]
    public void Given_UnassignedId_When_RoundTripped_Then_ItStaysNull()
    {
        var message = new TagDto { TenantId = TenantId, Name = "unsaved" }.ToProto();

        Assert.AreEqual(string.Empty, message.Id);
        Assert.IsFalse(message.HasVersion);
        Assert.IsNull(message.ToDto().Id);
        Assert.IsNull(message.ToDto().Version);
    }
}
