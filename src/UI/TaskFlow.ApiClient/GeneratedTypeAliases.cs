// OpenAPI (and therefore Refitter, generated via --interface-only) has no concept of an open generic
// type: Microsoft.AspNetCore.OpenApi names every closed generic schema "{Type}Of{Arg}" (verified against
// the committed document: DefaultResponse<TaskItemDto> -> "DefaultResponseOfTaskItemDto"). Rather than let
// Refitter synthesize its own duplicate DTOs for these wrapper envelopes (the project's stated fallback
// when it "must emit its own DTOs"), every flattened name below is aliased straight to the real shared
// generic type from TaskFlow.Application.Models / EF.Common.Contracts. The generated interface then
// compiles against the exact same types Blazor/React/Uno and the endpoint tests already use - zero
// duplicate contracts, zero edge-mapping code.
//
// Regenerate this list by running the Refitter command in docs/plans/client-generation.md, then diffing
// ITaskFlowApiClient.Generated.cs for any "XOfY" identifier not aliased here (a new entity or a new
// generic wrapper will need one line added).

global using DefaultResponseOfCategoryDto = TaskFlow.Application.Models.DefaultResponse<TaskFlow.Application.Models.CategoryDto>;
global using DefaultResponseOfTagDto = TaskFlow.Application.Models.DefaultResponse<TaskFlow.Application.Models.TagDto>;
global using DefaultResponseOfTaskItemDto = TaskFlow.Application.Models.DefaultResponse<TaskFlow.Application.Models.TaskItemDto>;
global using DefaultResponseOfCommentDto = TaskFlow.Application.Models.DefaultResponse<TaskFlow.Application.Models.CommentDto>;
global using DefaultResponseOfChecklistItemDto = TaskFlow.Application.Models.DefaultResponse<TaskFlow.Application.Models.ChecklistItemDto>;
global using DefaultResponseOfTaskItemTagDto = TaskFlow.Application.Models.DefaultResponse<TaskFlow.Application.Models.TaskItemTagDto>;
global using DefaultResponseOfAttachmentDto = TaskFlow.Application.Models.DefaultResponse<TaskFlow.Application.Models.AttachmentDto>;

global using DefaultRequestOfCategoryDto = TaskFlow.Application.Models.DefaultRequest<TaskFlow.Application.Models.CategoryDto>;
global using DefaultRequestOfTagDto = TaskFlow.Application.Models.DefaultRequest<TaskFlow.Application.Models.TagDto>;
global using DefaultRequestOfTaskItemDto = TaskFlow.Application.Models.DefaultRequest<TaskFlow.Application.Models.TaskItemDto>;
global using DefaultRequestOfTaskItemPatchDto = TaskFlow.Application.Models.DefaultRequest<TaskFlow.Application.Models.TaskItemPatchDto>;
global using DefaultRequestOfCommentDto = TaskFlow.Application.Models.DefaultRequest<TaskFlow.Application.Models.CommentDto>;
global using DefaultRequestOfChecklistItemDto = TaskFlow.Application.Models.DefaultRequest<TaskFlow.Application.Models.ChecklistItemDto>;
global using DefaultRequestOfAttachmentDto = TaskFlow.Application.Models.DefaultRequest<TaskFlow.Application.Models.AttachmentDto>;

global using PagedResponseOfCategoryDto = EF.Common.Contracts.PagedResponse<TaskFlow.Application.Models.CategoryDto>;
global using PagedResponseOfTagDto = EF.Common.Contracts.PagedResponse<TaskFlow.Application.Models.TagDto>;
global using PagedResponseOfAttachmentDto = EF.Common.Contracts.PagedResponse<TaskFlow.Application.Models.AttachmentDto>;

global using SearchRequestOfCategorySearchFilter = EF.Common.Contracts.SearchRequest<TaskFlow.Application.Models.CategorySearchFilter>;
global using SearchRequestOfTagSearchFilter = EF.Common.Contracts.SearchRequest<TaskFlow.Application.Models.TagSearchFilter>;
global using SearchRequestOfAttachmentSearchFilter = EF.Common.Contracts.SearchRequest<TaskFlow.Application.Models.AttachmentSearchFilter>;

global using CursorPageOfTaskItemDto = EF.Common.Contracts.CursorPage<TaskFlow.Application.Models.TaskItemDto>;
