using EF.Common.Contracts;
using TaskFlow.Application.Models.Shared;

namespace TaskFlow.Application.Models;

/// <summary>Carries tag data across API, application, and UI boundaries.</summary>
public record TagDto : EntityBaseDto, ITenantEntityDto
{
    public Guid TenantId { get; set; }
    public string Name { get; set; } = null!;
    public string? Color { get; set; }
}
