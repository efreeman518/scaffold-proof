using EF.Domain.Contracts;
using System.Linq.Expressions;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Model.ValueObjects;
using TaskFlow.Domain.Shared;

namespace TaskFlow.Application.Mappers;

/// <summary>Maps task item mapper domain objects and DTOs across application boundaries.</summary>
public static class TaskItemMapper
{
    // Canonical full shape. EF translates this server-side; the same expression is compiled
    // once at static init and reused for in-memory ToDto so the two call sites cannot drift.
    //
    // Inline child projections (no .ToDto() calls): EF cannot translate method-call delegates,
    // so the Expression form must construct child DTOs directly. The MapperTests parity check
    // verifies the compiled result still agrees with each child mapper's ToDto.
    //
    // Owned-type flattening (RecurrencePattern JSON -> scalar columns) must stay
    // EF-translatable AND evaluate correctly in-memory - keep these to property access and
    // null-conditional checks only.
    public static readonly Expression<Func<TaskItem, TaskItemDto>> Projection =
        entity => new TaskItemDto
        {
            Id = entity.Id.Value,
            Version = entity.Version,
            TenantId = entity.TenantId.Value,
            Title = entity.Title,
            Description = entity.Description,
            Priority = entity.Priority,
            Status = entity.Status,
            Features = entity.Features,
            EstimatedEffort = entity.EstimatedEffort,
            ActualEffort = entity.ActualEffort,
            CompletedDate = entity.CompletedDate,
            ModifiedAtUtc = entity.ModifiedAtUtc,
            CategoryId = entity.CategoryId.HasValue ? entity.CategoryId.Value.Value : null,
            ParentTaskItemId = entity.ParentTaskItemId.HasValue ? entity.ParentTaskItemId.Value.Value : null,
            CategoryName = entity.Category != null ? entity.Category.Name : null,
            StartDate = entity.StartDate,
            DueDate = entity.DueDate,
            RecurrenceInterval = entity.RecurrencePattern != null ? entity.RecurrencePattern.Interval : null,
            RecurrenceFrequency = entity.RecurrencePattern != null ? entity.RecurrencePattern.Frequency : null,
            RecurrenceEndDate = entity.RecurrencePattern != null ? entity.RecurrencePattern.EndDate : null,
            Comments = entity.Comments.Select(c => new CommentDto
            {
                Id = c.Id.Value,
                Version = c.Version,
                TenantId = c.TenantId.Value,
                Body = c.Body,
                TaskItemId = c.TaskItemId.Value
            }).ToList(),
            ChecklistItems = entity.ChecklistItems.Select(ci => new ChecklistItemDto
            {
                Id = ci.Id.Value,
                Version = ci.Version,
                TenantId = ci.TenantId.Value,
                Title = ci.Title,
                IsCompleted = ci.IsCompleted,
                SortOrder = ci.SortOrder,
                CompletedDate = ci.CompletedDate,
                TaskItemId = ci.TaskItemId.Value
            }).ToList(),
            Tags = entity.TaskItemTags.Select(tt => new TagDto
            {
                Id = tt.Tag!.Id.Value,
                Version = tt.Tag.Version,
                TenantId = tt.Tag.TenantId.Value,
                Name = tt.Tag.Name,
                Color = tt.Tag.Color
            }).ToList(),
            SubTasks = entity.SubTasks.Select(s => new TaskItemDto
            {
                Id = s.Id.Value,
                Version = s.Version,
                TenantId = s.TenantId.Value,
                Title = s.Title,
                Status = s.Status,
                Priority = s.Priority
            }).ToList()
        };

    private static readonly Func<TaskItem, TaskItemDto> Compiled = Projection.Compile();

    /// <summary>Converts the current value to DTO.</summary>
    public static TaskItemDto ToDto(this TaskItem entity) => Compiled(entity);

    /// <summary>Converts the current value to entity.</summary>
    public static DomainResult<TaskItem> ToEntity(this TaskItemDto dto, Guid tenantId)
    {
        var categoryId = DomainId.FromNullable<CategoryId>(dto.CategoryId);
        var parentTaskItemId = DomainId.FromNullable<TaskItemId>(dto.ParentTaskItemId);
        var result = TaskItem.Create(DomainId.From<TenantId>(tenantId), dto.Title, dto.Description, dto.Priority, categoryId, parentTaskItemId,
            id: DomainId.FromNullable<TaskItemId>(dto.Id));
        if (result.IsFailure) return result;

        var entity = result.Value!;
        entity.UpdateDateRange(dto.StartDate, dto.DueDate);

        if (dto.RecurrenceInterval.HasValue && !string.IsNullOrEmpty(dto.RecurrenceFrequency))
        {
            entity.UpdateRecurrencePattern(new RecurrencePattern
            {
                Interval = dto.RecurrenceInterval.Value,
                Frequency = dto.RecurrenceFrequency!,
                EndDate = dto.RecurrenceEndDate
            });
        }

        return DomainResult<TaskItem>.Success(entity);
    }

    // Minimal query-only shape for list/search endpoints. Intentionally has no ToDto counterpart
    // - search results should never include children. Kept separate from Projection because
    // the two have different purposes (lean grid rows vs. full hydrated record).
    public static readonly Expression<Func<TaskItem, TaskItemDto>> ProjectorSearch =
        entity => new TaskItemDto
        {
            Id = entity.Id.Value,
            Version = entity.Version,
            TenantId = entity.TenantId.Value,
            Title = entity.Title,
            Description = entity.Description,
            Priority = entity.Priority,
            Status = entity.Status,
            Features = entity.Features,
            EstimatedEffort = entity.EstimatedEffort,
            CategoryId = entity.CategoryId.HasValue ? entity.CategoryId.Value.Value : null,
            CategoryName = entity.Category != null ? entity.Category.Name : null,
            StartDate = entity.StartDate,
            DueDate = entity.DueDate,
            CompletedDate = entity.CompletedDate,
            ModifiedAtUtc = entity.ModifiedAtUtc
        };
}
