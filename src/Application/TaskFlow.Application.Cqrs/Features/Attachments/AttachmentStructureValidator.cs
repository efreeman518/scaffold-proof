using EF.Common.Contracts;
using EF.Domain.Contracts;
using TaskFlow.Application.Cqrs.Shared;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Shared.Constants;

namespace TaskFlow.Application.Cqrs.Features.Attachments;

/// <summary>Provides attachment structure validator behavior for the Features Attachments layer.</summary>
internal static class AttachmentStructureValidator
{
    /// <summary>Validates validate create rules and returns failures before work continues.</summary>
    public static Result<AttachmentDto> ValidateCreate(AttachmentDto dto)
    {
        var common = EntityDtoRules.ValidateCreate(dto);
        if (common.IsFailure) return Result<AttachmentDto>.Failure(common.Errors);

        var errors = new List<DomainError>();
        if (string.IsNullOrWhiteSpace(dto.FileName)) errors.Add(DomainError.Create("FileName is required."));
        if (dto.FileName?.Length > DomainConstants.RULE_ATTACHMENT_FILENAME_LENGTH_MAX) errors.Add(DomainError.Create($"FileName cannot exceed {DomainConstants.RULE_ATTACHMENT_FILENAME_LENGTH_MAX} characters."));
        if (string.IsNullOrWhiteSpace(dto.ContentType)) errors.Add(DomainError.Create("ContentType is required."));
        if (dto.ContentType?.Length > DomainConstants.RULE_ATTACHMENT_CONTENTTYPE_LENGTH_MAX) errors.Add(DomainError.Create($"ContentType cannot exceed {DomainConstants.RULE_ATTACHMENT_CONTENTTYPE_LENGTH_MAX} characters."));
        if (dto.FileSizeBytes <= 0) errors.Add(DomainError.Create("FileSizeBytes must be greater than zero."));
        if (string.IsNullOrWhiteSpace(dto.StorageUri)) errors.Add(DomainError.Create("StorageUri is required."));
        if (dto.StorageUri?.Length > DomainConstants.RULE_ATTACHMENT_STORAGEURI_LENGTH_MAX) errors.Add(DomainError.Create($"StorageUri cannot exceed {DomainConstants.RULE_ATTACHMENT_STORAGEURI_LENGTH_MAX} characters."));
        if (dto.OwnerId == Guid.Empty) errors.Add(DomainError.Create("OwnerId is required."));
        return errors.Count > 0 ? Result<AttachmentDto>.Failure(errors) : Result<AttachmentDto>.Success(dto);
    }

    /// <summary>Validates validate update rules and returns failures before work continues.</summary>
    public static Result<AttachmentDto> ValidateUpdate(AttachmentDto dto)
    {
        var common = EntityDtoRules.ValidateUpdate(dto);
        if (common.IsFailure) return Result<AttachmentDto>.Failure(common.Errors);

        var errors = new List<DomainError>();
        if (string.IsNullOrWhiteSpace(dto.FileName)) errors.Add(DomainError.Create("FileName is required."));
        if (dto.FileName?.Length > DomainConstants.RULE_ATTACHMENT_FILENAME_LENGTH_MAX) errors.Add(DomainError.Create($"FileName cannot exceed {DomainConstants.RULE_ATTACHMENT_FILENAME_LENGTH_MAX} characters."));
        if (string.IsNullOrWhiteSpace(dto.ContentType)) errors.Add(DomainError.Create("ContentType is required."));
        if (dto.ContentType?.Length > DomainConstants.RULE_ATTACHMENT_CONTENTTYPE_LENGTH_MAX) errors.Add(DomainError.Create($"ContentType cannot exceed {DomainConstants.RULE_ATTACHMENT_CONTENTTYPE_LENGTH_MAX} characters."));
        if (dto.FileSizeBytes <= 0) errors.Add(DomainError.Create("FileSizeBytes must be greater than zero."));
        if (string.IsNullOrWhiteSpace(dto.StorageUri)) errors.Add(DomainError.Create("StorageUri is required."));
        if (dto.StorageUri?.Length > DomainConstants.RULE_ATTACHMENT_STORAGEURI_LENGTH_MAX) errors.Add(DomainError.Create($"StorageUri cannot exceed {DomainConstants.RULE_ATTACHMENT_STORAGEURI_LENGTH_MAX} characters."));
        return errors.Count > 0 ? Result<AttachmentDto>.Failure(errors) : Result<AttachmentDto>.Success(dto);
    }
}
