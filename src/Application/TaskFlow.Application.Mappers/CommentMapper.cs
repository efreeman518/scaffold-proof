using EF.Domain.Contracts;
using System.Linq.Expressions;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;

namespace TaskFlow.Application.Mappers;

/// <summary>Maps comment mapper domain objects and DTOs across application boundaries.</summary>
public static class CommentMapper
{
    public static readonly Expression<Func<Comment, CommentDto>> Projection =
        entity => new CommentDto
        {
            Id = entity.Id.Value,
            Version = entity.Version,
            TenantId = entity.TenantId.Value,
            Body = entity.Body,
            TaskItemId = entity.TaskItemId.Value
        };

    private static readonly Func<Comment, CommentDto> Compiled = Projection.Compile();

    /// <summary>Converts the current value to DTO.</summary>
    public static CommentDto ToDto(this Comment entity) => Compiled(entity);

    /// <summary>Converts the current value to entity.</summary>
    public static DomainResult<Comment> ToEntity(this CommentDto dto, Guid tenantId)
        => Comment.Create(DomainId.From<TenantId>(tenantId), DomainId.From<TaskItemId>(dto.TaskItemId), dto.Body,
            DomainId.FromNullable<CommentId>(dto.Id));
}
