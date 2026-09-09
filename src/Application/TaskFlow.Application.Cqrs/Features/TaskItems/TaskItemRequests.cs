using EF.Common.Contracts;
using EF.CQRS.Abstractions;
using TaskFlow.Application.Models;
using TaskFlow.Application.Models.Paging;

namespace TaskFlow.Application.Cqrs.Features.TaskItems;

/// <summary>
/// Carries search task items query CQRS data between endpoints and handlers. Cursor decoding and
/// minting stay inside the handler so both endpoint styles pass the identical request shape; putting
/// a decoded CursorToken on the query would force the CQRS endpoint to differ from the Service one,
/// which is exactly the parity the dual-style proof exists to hold.
/// </summary>
public sealed record SearchTaskItemsQuery(TaskItemCursorSearchRequest Request)
    : IQuery<CursorPage<TaskItemDto>>;

/// <summary>Carries get task item by ID query CQRS data between endpoints and handlers.</summary>
public sealed record GetTaskItemByIdQuery(Guid Id)
    : IQuery<Result<DefaultResponse<TaskItemDto>>>;

/// <summary>Carries create task item command CQRS data between endpoints and handlers.</summary>
public sealed record CreateTaskItemCommand(DefaultRequest<TaskItemDto> Request)
    : ICommand<Result<DefaultResponse<TaskItemDto>>>;

/// <summary>Carries update task item command CQRS data between endpoints and handlers.</summary>
public sealed record UpdateTaskItemCommand(DefaultRequest<TaskItemDto> Request, long? ExpectedVersion)
    : ICommand<Result<DefaultResponse<TaskItemDto>>>;

/// <summary>
/// Carries patch task item command CQRS data between endpoints and handlers. PATCH previously existed
/// only in the Service style, so the same triage workflow 404'd under CQRS - this closes that gap.
/// </summary>
public sealed record PatchTaskItemCommand(Guid Id, TaskItemPatchDto Patch, long? ExpectedVersion)
    : ICommand<Result<DefaultResponse<TaskItemDto>>>;

/// <summary>Carries delete task item command CQRS data between endpoints and handlers.</summary>
public sealed record DeleteTaskItemCommand(Guid Id, long? ExpectedVersion)
    : ICommand<Result>;
