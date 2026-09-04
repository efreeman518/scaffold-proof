using EF.Common.Contracts;
using TaskFlow.Application.Cqrs.Registration;
using TaskFlow.Application.Models;
using TaskFlow.Application.Models.Paging;

namespace TaskFlow.Application.Cqrs.Features.TaskItems;

/// <summary>Provides task item CQRS registrations behavior for the Features Task Items layer.</summary>
internal static class TaskItemCqrsRegistrations
{
    public static IReadOnlyList<CqrsHandlerRegistration> Registrations { get; } =
    [
        new(typeof(SearchTaskItemsQuery), typeof(CursorPage<TaskItemDto>), typeof(SearchTaskItemsHandler)),
        new(typeof(GetTaskItemByIdQuery), typeof(Result<DefaultResponse<TaskItemDto>>), typeof(GetTaskItemByIdHandler)),
        new(typeof(CreateTaskItemCommand), typeof(Result<DefaultResponse<TaskItemDto>>), typeof(CreateTaskItemHandler)),
        new(typeof(UpdateTaskItemCommand), typeof(Result<DefaultResponse<TaskItemDto>>), typeof(UpdateTaskItemHandler)),
        new(typeof(PatchTaskItemCommand), typeof(Result<DefaultResponse<TaskItemDto>>), typeof(PatchTaskItemHandler)),
        new(typeof(DeleteTaskItemCommand), typeof(Result), typeof(DeleteTaskItemHandler)),

        // Nested children mutated through the aggregate root (GR-15) - no standalone child handlers.
        new(typeof(AddTaskItemCommentCommand), typeof(Result<DefaultResponse<CommentDto>>), typeof(AddTaskItemCommentHandler)),
        new(typeof(UpdateTaskItemCommentCommand), typeof(Result<DefaultResponse<CommentDto>>), typeof(UpdateTaskItemCommentHandler)),
        new(typeof(RemoveTaskItemCommentCommand), typeof(Result), typeof(RemoveTaskItemCommentHandler)),
        new(typeof(AddTaskItemChecklistItemCommand), typeof(Result<DefaultResponse<ChecklistItemDto>>), typeof(AddTaskItemChecklistItemHandler)),
        new(typeof(UpdateTaskItemChecklistItemCommand), typeof(Result<DefaultResponse<ChecklistItemDto>>), typeof(UpdateTaskItemChecklistItemHandler)),
        new(typeof(RemoveTaskItemChecklistItemCommand), typeof(Result), typeof(RemoveTaskItemChecklistItemHandler)),
        new(typeof(AssociateTaskItemTagCommand), typeof(Result<DefaultResponse<TaskItemTagDto>>), typeof(AssociateTaskItemTagHandler)),
        new(typeof(RemoveTaskItemTagCommand), typeof(Result), typeof(RemoveTaskItemTagHandler)),
    ];
}
