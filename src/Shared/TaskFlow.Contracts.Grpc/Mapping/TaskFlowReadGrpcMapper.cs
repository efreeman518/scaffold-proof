using Google.Protobuf.WellKnownTypes;
using System.Globalization;
using TaskFlow.Application.Models;
using TaskFlow.Application.Models.Reads;
using DomainEnums = TaskFlow.Domain.Shared.Enums;

namespace TaskFlow.Contracts.Grpc;

/// <summary>
/// The single conversion between the REST/application DTOs and the generated protobuf messages
/// (D-054). It lives in the contract assembly rather than in either host because both ends need it:
/// the Api maps DTO to proto on the way out, the Blazor host maps proto back to DTO so its existing
/// call sites keep working against one shape.
///
/// Three conversions are lossy by construction, and every one of them is a deliberate choice made in
/// taskflow_read.proto rather than an accident here:
/// <list type="bullet">
/// <item>DateTimeOffset -> Timestamp keeps the instant and drops the offset. Round-tripping yields the
/// same UTC instant with a zero offset; TaskFlow stores and compares in UTC everywhere, so nothing
/// reads the original offset.</item>
/// <item>decimal travels as an invariant-culture string, because proto has no decimal and double would
/// silently change the value.</item>
/// <item>A null collection and an empty collection both become an empty repeated field, and come back
/// as an empty list. proto3 cannot express the difference, and no read call site distinguishes them.</item>
/// </list>
/// </summary>
public static class TaskFlowReadGrpcMapper
{
    // ---- Summary -------------------------------------------------------------------------------

    /// <summary>Projects the tenant task summary onto the wire message.</summary>
    public static TaskItemSummary ToProto(this TaskItemSummaryDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var message = new TaskItemSummary
        {
            Overdue = dto.Overdue,
            Total = dto.Total,
            GeneratedAtUtc = Timestamp.FromDateTimeOffset(dto.GeneratedAtUtc)
        };

        foreach (var bucket in dto.ByStatus)
        {
            message.ByStatus.Add(new TaskItemStatusCount
            {
                Status = (TaskItemStatus)(int)bucket.Status,
                Count = bucket.Count
            });
        }

        return message;
    }

    /// <summary>Reads the wire message back into the DTO the UI call sites already use.</summary>
    public static TaskItemSummaryDto ToDto(this TaskItemSummary message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new TaskItemSummaryDto
        {
            ByStatus = [.. message.ByStatus.Select(b =>
                new TaskItemStatusCountDto((DomainEnums.TaskItemStatus)(int)b.Status, b.Count))],
            Overdue = message.Overdue,
            Total = message.Total,
            GeneratedAtUtc = ToDateTimeOffset(message.GeneratedAtUtc) ?? default
        };
    }

    // ---- Metadata ------------------------------------------------------------------------------

    /// <summary>Projects the category and tag picker lists onto the wire message.</summary>
    public static TaskMetadata ToProto(this TaskMetadataDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var message = new TaskMetadata
        {
            GeneratedAtUtc = Timestamp.FromDateTimeOffset(dto.GeneratedAtUtc)
        };

        foreach (var category in dto.Categories) message.Categories.Add(ToProto(category));
        foreach (var tag in dto.Tags) message.Tags.Add(ToProto(tag));

        return message;
    }

    /// <summary>Reads the picker lists back into the DTO shape.</summary>
    public static TaskMetadataDto ToDto(this TaskMetadata message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new TaskMetadataDto
        {
            Categories = [.. message.Categories.Select(ToDto)],
            Tags = [.. message.Tags.Select(ToDto)],
            GeneratedAtUtc = ToDateTimeOffset(message.GeneratedAtUtc) ?? default
        };
    }

    // ---- Task item -----------------------------------------------------------------------------

    /// <summary>Projects one task item, including its loaded children, onto the wire message.</summary>
    public static TaskItem ToProto(this TaskItemDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var message = new TaskItem
        {
            Id = FromGuid(dto.Id),
            TenantId = FromGuid(dto.TenantId),
            Title = dto.Title ?? string.Empty,
            Priority = (Priority)(int)dto.Priority,
            Status = (TaskItemStatus)(int)dto.Status,
            Features = (int)dto.Features
        };

        SetVersion(dto.Version, v => message.Version = v);
        SetOptional(dto.Description, v => message.Description = v);
        SetOptional(FromDecimal(dto.EstimatedEffort), v => message.EstimatedEffort = v);
        SetOptional(FromDecimal(dto.ActualEffort), v => message.ActualEffort = v);
        SetOptional(dto.CompletedDate, v => message.CompletedDate = v);
        SetOptional(dto.ModifiedAtUtc, v => message.ModifiedAtUtc = v);
        SetOptional(FromNullableGuid(dto.CategoryId), v => message.CategoryId = v);
        SetOptional(FromNullableGuid(dto.ParentTaskItemId), v => message.ParentTaskItemId = v);
        SetOptional(dto.StartDate, v => message.StartDate = v);
        SetOptional(dto.DueDate, v => message.DueDate = v);
        if (dto.RecurrenceInterval.HasValue) message.RecurrenceInterval = dto.RecurrenceInterval.Value;
        SetOptional(dto.RecurrenceFrequency, v => message.RecurrenceFrequency = v);
        SetOptional(dto.RecurrenceEndDate, v => message.RecurrenceEndDate = v);
        SetOptional(dto.CategoryName, v => message.CategoryName = v);

        if (dto.Comments is not null) foreach (var c in dto.Comments) message.Comments.Add(ToProto(c));
        if (dto.ChecklistItems is not null) foreach (var c in dto.ChecklistItems) message.ChecklistItems.Add(ToProto(c));
        if (dto.Tags is not null) foreach (var t in dto.Tags) message.Tags.Add(ToProto(t));
        if (dto.Attachments is not null) foreach (var a in dto.Attachments) message.Attachments.Add(ToProto(a));
        if (dto.SubTasks is not null) foreach (var s in dto.SubTasks) message.SubTasks.Add(ToProto(s));

        return message;
    }

    /// <summary>Reads one task item, including its children, back into the DTO shape.</summary>
    public static TaskItemDto ToDto(this TaskItem message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new TaskItemDto
        {
            Id = ToNullableGuid(message.Id),
            Version = message.HasVersion ? message.Version : null,
            TenantId = ToGuid(message.TenantId),
            Title = message.Title,
            Description = message.HasDescription ? message.Description : null,
            Priority = (DomainEnums.Priority)(int)message.Priority,
            Status = (DomainEnums.TaskItemStatus)(int)message.Status,
            Features = (DomainEnums.TaskFeatures)message.Features,
            EstimatedEffort = ToDecimal(message.HasEstimatedEffort ? message.EstimatedEffort : null),
            ActualEffort = ToDecimal(message.HasActualEffort ? message.ActualEffort : null),
            CompletedDate = ToDateTimeOffset(message.CompletedDate),
            ModifiedAtUtc = ToDateTimeOffset(message.ModifiedAtUtc),
            CategoryId = ToNullableGuid(message.HasCategoryId ? message.CategoryId : null),
            ParentTaskItemId = ToNullableGuid(message.HasParentTaskItemId ? message.ParentTaskItemId : null),
            StartDate = ToDateTimeOffset(message.StartDate),
            DueDate = ToDateTimeOffset(message.DueDate),
            RecurrenceInterval = message.HasRecurrenceInterval ? message.RecurrenceInterval : null,
            RecurrenceFrequency = message.HasRecurrenceFrequency ? message.RecurrenceFrequency : null,
            RecurrenceEndDate = ToDateTimeOffset(message.RecurrenceEndDate),
            CategoryName = message.HasCategoryName ? message.CategoryName : null,
            Comments = [.. message.Comments.Select(ToDto)],
            ChecklistItems = [.. message.ChecklistItems.Select(ToDto)],
            Tags = [.. message.Tags.Select(ToDto)],
            Attachments = [.. message.Attachments.Select(ToDto)],
            SubTasks = [.. message.SubTasks.Select(ToDto)]
        };
    }

    // ---- Child shapes --------------------------------------------------------------------------

    /// <summary>Projects a category onto the wire message.</summary>
    public static Category ToProto(this CategoryDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var message = new Category
        {
            Id = FromGuid(dto.Id),
            TenantId = FromGuid(dto.TenantId),
            Name = dto.Name ?? string.Empty,
            SortOrder = dto.SortOrder,
            IsActive = dto.IsActive
        };

        SetVersion(dto.Version, v => message.Version = v);
        SetOptional(dto.Description, v => message.Description = v);
        SetOptional(FromNullableGuid(dto.ParentCategoryId), v => message.ParentCategoryId = v);
        return message;
    }

    /// <summary>Reads a category back into the DTO shape.</summary>
    public static CategoryDto ToDto(this Category message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new CategoryDto
        {
            Id = ToNullableGuid(message.Id),
            Version = message.HasVersion ? message.Version : null,
            TenantId = ToGuid(message.TenantId),
            Name = message.Name,
            Description = message.HasDescription ? message.Description : null,
            SortOrder = message.SortOrder,
            IsActive = message.IsActive,
            ParentCategoryId = ToNullableGuid(message.HasParentCategoryId ? message.ParentCategoryId : null)
        };
    }

    /// <summary>Projects a tag onto the wire message.</summary>
    public static Tag ToProto(this TagDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var message = new Tag
        {
            Id = FromGuid(dto.Id),
            TenantId = FromGuid(dto.TenantId),
            Name = dto.Name ?? string.Empty
        };

        SetVersion(dto.Version, v => message.Version = v);
        SetOptional(dto.Color, v => message.Color = v);
        return message;
    }

    /// <summary>Reads a tag back into the DTO shape.</summary>
    public static TagDto ToDto(this Tag message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new TagDto
        {
            Id = ToNullableGuid(message.Id),
            Version = message.HasVersion ? message.Version : null,
            TenantId = ToGuid(message.TenantId),
            Name = message.Name,
            Color = message.HasColor ? message.Color : null
        };
    }

    /// <summary>Projects a checklist item onto the wire message.</summary>
    public static ChecklistItem ToProto(this ChecklistItemDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var message = new ChecklistItem
        {
            Id = FromGuid(dto.Id),
            TenantId = FromGuid(dto.TenantId),
            Title = dto.Title ?? string.Empty,
            IsCompleted = dto.IsCompleted,
            SortOrder = dto.SortOrder,
            TaskItemId = FromGuid(dto.TaskItemId)
        };

        SetVersion(dto.Version, v => message.Version = v);
        SetOptional(dto.CompletedDate, v => message.CompletedDate = v);
        return message;
    }

    /// <summary>Reads a checklist item back into the DTO shape.</summary>
    public static ChecklistItemDto ToDto(this ChecklistItem message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new ChecklistItemDto
        {
            Id = ToNullableGuid(message.Id),
            Version = message.HasVersion ? message.Version : null,
            TenantId = ToGuid(message.TenantId),
            Title = message.Title,
            IsCompleted = message.IsCompleted,
            SortOrder = message.SortOrder,
            CompletedDate = ToDateTimeOffset(message.CompletedDate),
            TaskItemId = ToGuid(message.TaskItemId)
        };
    }

    /// <summary>Projects an attachment onto the wire message.</summary>
    public static Attachment ToProto(this AttachmentDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var message = new Attachment
        {
            Id = FromGuid(dto.Id),
            TenantId = FromGuid(dto.TenantId),
            FileName = dto.FileName ?? string.Empty,
            ContentType = dto.ContentType ?? string.Empty,
            FileSizeBytes = dto.FileSizeBytes,
            StorageUri = dto.StorageUri ?? string.Empty,
            OwnerType = (AttachmentOwnerType)(int)dto.OwnerType,
            OwnerId = FromGuid(dto.OwnerId)
        };

        SetVersion(dto.Version, v => message.Version = v);
        return message;
    }

    /// <summary>Reads an attachment back into the DTO shape.</summary>
    public static AttachmentDto ToDto(this Attachment message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new AttachmentDto
        {
            Id = ToNullableGuid(message.Id),
            Version = message.HasVersion ? message.Version : null,
            TenantId = ToGuid(message.TenantId),
            FileName = message.FileName,
            ContentType = message.ContentType,
            FileSizeBytes = message.FileSizeBytes,
            StorageUri = message.StorageUri,
            OwnerType = (DomainEnums.AttachmentOwnerType)(int)message.OwnerType,
            OwnerId = ToGuid(message.OwnerId)
        };
    }

    /// <summary>Projects a comment, including its attachments, onto the wire message.</summary>
    public static Comment ToProto(this CommentDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var message = new Comment
        {
            Id = FromGuid(dto.Id),
            TenantId = FromGuid(dto.TenantId),
            Body = dto.Body ?? string.Empty,
            TaskItemId = FromGuid(dto.TaskItemId)
        };

        SetVersion(dto.Version, v => message.Version = v);
        if (dto.Attachments is not null)
            foreach (var attachment in dto.Attachments) message.Attachments.Add(ToProto(attachment));

        return message;
    }

    /// <summary>Reads a comment, including its attachments, back into the DTO shape.</summary>
    public static CommentDto ToDto(this Comment message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new CommentDto
        {
            Id = ToNullableGuid(message.Id),
            Version = message.HasVersion ? message.Version : null,
            TenantId = ToGuid(message.TenantId),
            Body = message.Body,
            TaskItemId = ToGuid(message.TaskItemId),
            Attachments = [.. message.Attachments.Select(ToDto)]
        };
    }

    // ---- Scalar conversions --------------------------------------------------------------------

    /// <summary>Assigns an optional scalar only when it is present: the generated setters reject null.</summary>
    private static void SetOptional(string? value, Action<string> assign)
    {
        if (value is not null) assign(value);
    }

    /// <summary>Assigns an optional timestamp only when the source has a value.</summary>
    private static void SetOptional(DateTimeOffset? value, Action<Timestamp> assign)
    {
        if (value.HasValue) assign(Timestamp.FromDateTimeOffset(value.Value));
    }

    /// <summary>Assigns the aggregate concurrency token only when the DTO carries one.</summary>
    private static void SetVersion(long? version, Action<long> assign)
    {
        if (version.HasValue) assign(version.Value);
    }

    /// <summary>Canonical "D" form, or empty for an unset id. See the header note on GUID encoding.</summary>
    private static string FromGuid(Guid value) => value.ToString();

    private static string FromGuid(Guid? value) => value?.ToString() ?? string.Empty;

    private static string? FromNullableGuid(Guid? value) => value?.ToString();

    private static Guid ToGuid(string value) => Guid.TryParse(value, out var parsed) ? parsed : Guid.Empty;

    private static Guid? ToNullableGuid(string? value) =>
        !string.IsNullOrEmpty(value) && Guid.TryParse(value, out var parsed) ? parsed : null;

    private static string? FromDecimal(decimal? value) =>
        value?.ToString(CultureInfo.InvariantCulture);

    private static decimal? ToDecimal(string? value) =>
        !string.IsNullOrEmpty(value) && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    /// <summary>An unset Timestamp field is null on the message, which is the DTO's null.</summary>
    private static DateTimeOffset? ToDateTimeOffset(Timestamp? value) => value?.ToDateTimeOffset();
}
