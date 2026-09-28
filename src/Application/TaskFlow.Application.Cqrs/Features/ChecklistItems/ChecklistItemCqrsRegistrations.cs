using EF.Common.Contracts;
using EF.CQRS.DependencyInjection;
using TaskFlow.Application.Models;

namespace TaskFlow.Application.Cqrs.Features.ChecklistItems;

/// <summary>Provides checklist item CQRS registrations behavior for the Features Checklist Items layer.</summary>
internal static class ChecklistItemCqrsRegistrations
{
    public static IReadOnlyList<RequestHandlerRegistration> Registrations { get; } =
    [
        new(typeof(SearchChecklistItemsQuery), typeof(PagedResponse<ChecklistItemDto>), typeof(SearchChecklistItemsHandler)),
        new(typeof(GetChecklistItemByIdQuery), typeof(Result<DefaultResponse<ChecklistItemDto>>), typeof(GetChecklistItemByIdHandler)),
    ];
}
