using EF.Common.Contracts;
using TaskFlow.Application.Models.Shared;

namespace TaskFlow.Application.Models;

/// <summary>Carries task item tag data across API, application, and UI boundaries.</summary>
public record TaskItemTagDto : EntityBaseDto, ITenantEntityDto
{
    public Guid TenantId { get; set; }
    public Guid TaskItemId { get; set; }
    public Guid TagId { get; set; }
}
