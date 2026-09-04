using EF.Domain.Contracts;
using System.Linq.Expressions;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;

namespace TaskFlow.Application.Mappers;

/// <summary>Maps category mapper domain objects and DTOs across application boundaries.</summary>
public static class CategoryMapper
{
    public static readonly Expression<Func<Category, CategoryDto>> Projection =
        entity => new CategoryDto
        {
            Id = entity.Id.Value,
            Version = entity.Version,
            TenantId = entity.TenantId.Value,
            Name = entity.Name,
            Description = entity.Description,
            SortOrder = entity.SortOrder,
            IsActive = entity.IsActive,
            ParentCategoryId = entity.ParentCategoryId.HasValue ? entity.ParentCategoryId.Value.Value : null
        };

    private static readonly Func<Category, CategoryDto> Compiled = Projection.Compile();

    /// <summary>Converts the current value to DTO.</summary>
    public static CategoryDto ToDto(this Category entity) => Compiled(entity);

    /// <summary>Converts the current value to entity.</summary>
    public static DomainResult<Category> ToEntity(this CategoryDto dto, Guid tenantId)
    {
        var parentCategoryId = DomainId.FromNullable<CategoryId>(dto.ParentCategoryId);
        return Category.Create(DomainId.From<TenantId>(tenantId), dto.Name, dto.Description, dto.SortOrder, parentCategoryId,
            DomainId.FromNullable<CategoryId>(dto.Id));
    }
}
