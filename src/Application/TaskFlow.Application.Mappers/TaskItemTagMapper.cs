using EF.Domain.Contracts;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;

namespace TaskFlow.Application.Mappers;

/// <summary>Maps task item tag mapper domain objects and DTOs across application boundaries.</summary>
public static class TaskItemTagMapper
{
    /// <summary>Converts the current value to DTO.</summary>
    public static TaskItemTagDto ToDto(this TaskItemTag entity) => new()
    {
        Id = entity.Id.Value,
        Version = entity.Version,
        TenantId = entity.TenantId.Value,
        TaskItemId = entity.TaskItemId.Value,
        TagId = entity.TagId.Value
    };

    /// <summary>Converts the current value to entity.</summary>
    public static DomainResult<TaskItemTag> ToEntity(this TaskItemTagDto dto, Guid tenantId)
        => TaskItemTag.Create(DomainId.From<TenantId>(tenantId), DomainId.From<TaskItemId>(dto.TaskItemId), DomainId.From<TagId>(dto.TagId));
}
