using EF.Common.Contracts;
using EF.Domain.Contracts;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Shared.Constants;

namespace TaskFlow.Application.Services.Rules;

/// <summary>Provides attachment structure validator behavior for the Application Rules layer.</summary>
internal static class AttachmentStructureValidator
{
    /// <summary>Validates validate create rules and returns failures before work continues.</summary>
    public static Result<AttachmentDto> ValidateCreate(AttachmentDto dto)
    {
        var common = EntityDtoRules.ValidateCreate(dto);
        if (common.TryGetValue(out Error? commonError)) return commonError;

        var errors = new List<ValidationIssue>();
        if (string.IsNullOrWhiteSpace(dto.FileName)) errors.Add(new ValidationIssue("FileName is required.", nameof(AttachmentDto.FileName), "file_name.required"));
        if (dto.FileName?.Length > DomainConstants.RULE_ATTACHMENT_FILENAME_LENGTH_MAX) errors.Add(new ValidationIssue($"FileName cannot exceed {DomainConstants.RULE_ATTACHMENT_FILENAME_LENGTH_MAX} characters.", nameof(AttachmentDto.FileName), "file_name.max_length"));
        if (string.IsNullOrWhiteSpace(dto.ContentType)) errors.Add(new ValidationIssue("ContentType is required.", nameof(AttachmentDto.ContentType), "content_type.required"));
        if (dto.ContentType?.Length > DomainConstants.RULE_ATTACHMENT_CONTENTTYPE_LENGTH_MAX) errors.Add(new ValidationIssue($"ContentType cannot exceed {DomainConstants.RULE_ATTACHMENT_CONTENTTYPE_LENGTH_MAX} characters.", nameof(AttachmentDto.ContentType), "content_type.max_length"));
        if (dto.FileSizeBytes <= 0) errors.Add(new ValidationIssue("FileSizeBytes must be greater than zero.", nameof(AttachmentDto.FileSizeBytes), "file_size_bytes.positive"));
        if (string.IsNullOrWhiteSpace(dto.StorageUri)) errors.Add(new ValidationIssue("StorageUri is required.", nameof(AttachmentDto.StorageUri), "storage_uri.required"));
        if (dto.StorageUri?.Length > DomainConstants.RULE_ATTACHMENT_STORAGEURI_LENGTH_MAX) errors.Add(new ValidationIssue($"StorageUri cannot exceed {DomainConstants.RULE_ATTACHMENT_STORAGEURI_LENGTH_MAX} characters.", nameof(AttachmentDto.StorageUri), "storage_uri.max_length"));
        if (dto.OwnerId == Guid.Empty) errors.Add(new ValidationIssue("OwnerId is required.", nameof(AttachmentDto.OwnerId), "owner_id.required"));
        return errors.Count > 0 ? new ValidationError(errors) : dto;
    }

    /// <summary>Validates validate update rules and returns failures before work continues.</summary>
    public static Result<AttachmentDto> ValidateUpdate(AttachmentDto dto)
    {
        var common = EntityDtoRules.ValidateUpdate(dto);
        if (common.TryGetValue(out Error? commonError)) return commonError;

        var errors = new List<ValidationIssue>();
        if (string.IsNullOrWhiteSpace(dto.FileName)) errors.Add(new ValidationIssue("FileName is required.", nameof(AttachmentDto.FileName), "file_name.required"));
        if (dto.FileName?.Length > DomainConstants.RULE_ATTACHMENT_FILENAME_LENGTH_MAX) errors.Add(new ValidationIssue($"FileName cannot exceed {DomainConstants.RULE_ATTACHMENT_FILENAME_LENGTH_MAX} characters.", nameof(AttachmentDto.FileName), "file_name.max_length"));
        if (string.IsNullOrWhiteSpace(dto.ContentType)) errors.Add(new ValidationIssue("ContentType is required.", nameof(AttachmentDto.ContentType), "content_type.required"));
        if (dto.ContentType?.Length > DomainConstants.RULE_ATTACHMENT_CONTENTTYPE_LENGTH_MAX) errors.Add(new ValidationIssue($"ContentType cannot exceed {DomainConstants.RULE_ATTACHMENT_CONTENTTYPE_LENGTH_MAX} characters.", nameof(AttachmentDto.ContentType), "content_type.max_length"));
        if (dto.FileSizeBytes <= 0) errors.Add(new ValidationIssue("FileSizeBytes must be greater than zero.", nameof(AttachmentDto.FileSizeBytes), "file_size_bytes.positive"));
        if (string.IsNullOrWhiteSpace(dto.StorageUri)) errors.Add(new ValidationIssue("StorageUri is required.", nameof(AttachmentDto.StorageUri), "storage_uri.required"));
        if (dto.StorageUri?.Length > DomainConstants.RULE_ATTACHMENT_STORAGEURI_LENGTH_MAX) errors.Add(new ValidationIssue($"StorageUri cannot exceed {DomainConstants.RULE_ATTACHMENT_STORAGEURI_LENGTH_MAX} characters.", nameof(AttachmentDto.StorageUri), "storage_uri.max_length"));
        return errors.Count > 0 ? new ValidationError(errors) : dto;
    }
}
