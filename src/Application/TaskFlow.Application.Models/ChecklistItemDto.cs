using EF.Common.Contracts;
using TaskFlow.Application.Models.Shared;

namespace TaskFlow.Application.Models;

/// <summary>Carries checklist item data across API, application, and UI boundaries.</summary>
public record ChecklistItemDto : EntityBaseDto, ITenantEntityDto
{
    public Guid TenantId { get; set; }
    public string Title { get; set; } = null!;
    public bool IsCompleted { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset? CompletedDate { get; set; }
    public Guid TaskItemId { get; set; }
}
