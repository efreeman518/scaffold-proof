using EF.Common.Contracts;
using EF.CQRS.Abstractions;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Shared.Enums;

namespace TaskFlow.Application.Cqrs.Features.Attachments;

/// <summary>Carries search attachments query CQRS data between endpoints and handlers.</summary>
public sealed record SearchAttachmentsQuery(SearchRequest<AttachmentSearchFilter> Request, bool IncludeTotal = false)
    : IQuery<PagedResponse<AttachmentDto>>;

/// <summary>Carries get attachment by ID query CQRS data between endpoints and handlers.</summary>
public sealed record GetAttachmentByIdQuery(Guid Id)
    : IQuery<Result<DefaultResponse<AttachmentDto>>>;

/// <summary>Carries create attachment command CQRS data between endpoints and handlers.</summary>
public sealed record CreateAttachmentCommand(DefaultRequest<AttachmentDto> Request)
    : ICommand<Result<DefaultResponse<AttachmentDto>>>;

/// <summary>Carries upload attachment command CQRS data between endpoints and handlers.</summary>
public sealed record UploadAttachmentCommand(
    Stream FileStream,
    string FileName,
    string ContentType,
    long FileSizeBytes,
    AttachmentOwnerType OwnerType,
    Guid OwnerId,
    Guid? Id = null)
    : ICommand<Result<DefaultResponse<AttachmentDto>>>;

/// <summary>Carries update attachment command CQRS data between endpoints and handlers.</summary>
public sealed record UpdateAttachmentCommand(DefaultRequest<AttachmentDto> Request, long? ExpectedVersion)
    : ICommand<Result<DefaultResponse<AttachmentDto>>>;

/// <summary>Carries delete attachment command CQRS data between endpoints and handlers.</summary>
public sealed record DeleteAttachmentCommand(Guid Id, long? ExpectedVersion)
    : ICommand<Result>;
