using EF.Common.Contracts;
using EF.CQRS.Abstractions;
using TaskFlow.Application.Models;

namespace TaskFlow.Application.Cqrs.Features.ChecklistItems;

/// <summary>Carries search checklist items query CQRS data between endpoints and handlers.</summary>
public sealed record SearchChecklistItemsQuery(SearchRequest<ChecklistItemSearchFilter> Request, bool IncludeTotal = false)
    : IQuery<PagedResponse<ChecklistItemDto>>;

/// <summary>Carries get checklist item by ID query CQRS data between endpoints and handlers.</summary>
public sealed record GetChecklistItemByIdQuery(Guid Id)
    : IQuery<Result<DefaultResponse<ChecklistItemDto>>>;
