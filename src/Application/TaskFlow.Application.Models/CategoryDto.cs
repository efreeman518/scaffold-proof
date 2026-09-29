using EF.Common.Contracts;
using TaskFlow.Application.Models.Shared;

namespace TaskFlow.Application.Models;

/// <summary>Carries category data across API, application, and UI boundaries.</summary>
public record CategoryDto : EntityBaseDto, ITenantEntityDto
{
    public Guid TenantId { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
    public Guid? ParentCategoryId { get; set; }
}
