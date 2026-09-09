using EF.Domain.Contracts;
using System.Linq.Expressions;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;

namespace TaskFlow.Application.Mappers;

/// <summary>Maps checklist item mapper domain objects and DTOs across application boundaries.</summary>
public static class ChecklistItemMapper
{
    // Canonical full shape. EF translates this server-side; the same expression is compiled
    // once at static init and reused for in-memory ToDto so the two call sites cannot drift.
    public static readonly Expression<Func<ChecklistItem, ChecklistItemDto>> Projection =
        entity => new ChecklistItemDto
        {
            Id = entity.Id.Value,
            Version = entity.Version,
            TenantId = entity.TenantId.Value,
            Title = entity.Title,
            IsCompleted = entity.IsCompleted,
            SortOrder = entity.SortOrder,
            CompletedDate = entity.CompletedDate,
            TaskItemId = entity.TaskItemId.Value
        };

    private static readonly Func<ChecklistItem, ChecklistItemDto> Compiled = Projection.Compile();

    /// <summary>Converts the current value to DTO.</summary>
    public static ChecklistItemDto ToDto(this ChecklistItem entity) => Compiled(entity);

    /// <summary>Converts the current value to entity.</summary>
    public static DomainResult<ChecklistItem> ToEntity(this ChecklistItemDto dto, Guid tenantId)
        => ChecklistItem.Create(DomainId.From<TenantId>(tenantId), DomainId.From<TaskItemId>(dto.TaskItemId), dto.Title, dto.SortOrder,
            DomainId.FromNullable<ChecklistItemId>(dto.Id));
}
