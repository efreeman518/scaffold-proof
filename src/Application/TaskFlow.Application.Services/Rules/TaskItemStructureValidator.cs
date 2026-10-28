using EF.Common.Contracts;
using EF.Domain.Contracts;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Shared.Constants;

namespace TaskFlow.Application.Services.Rules;

/// <summary>Provides task item structure validator behavior for the Application Rules layer.</summary>
internal static class TaskItemStructureValidator
{
    /// <summary>Validates validate create rules and returns failures before work continues.</summary>
    public static Result<TaskItemDto> ValidateCreate(TaskItemDto dto)
    {
        var common = EntityDtoRules.ValidateCreate(dto);
        if (common.TryGetValue(out Error? commonError)) return commonError;

        var errors = new List<ValidationIssue>();
        if (string.IsNullOrWhiteSpace(dto.Title)) errors.Add(new ValidationIssue("Title is required.", nameof(TaskItemDto.Title), "title.required"));
        if (dto.Title?.Length > DomainConstants.RULE_DEFAULT_NAME_LENGTH_MAX) errors.Add(new ValidationIssue($"Title cannot exceed {DomainConstants.RULE_DEFAULT_NAME_LENGTH_MAX} characters.", nameof(TaskItemDto.Title), "title.max_length"));
        if (dto.Description?.Length > DomainConstants.RULE_DEFAULT_DESCRIPTION_LENGTH_MAX) errors.Add(new ValidationIssue($"Description cannot exceed {DomainConstants.RULE_DEFAULT_DESCRIPTION_LENGTH_MAX} characters.", nameof(TaskItemDto.Description), "description.max_length"));
        return errors.Count > 0 ? new ValidationError(errors) : dto;
    }

    /// <summary>Validates validate update rules and returns failures before work continues.</summary>
    public static Result<TaskItemDto> ValidateUpdate(TaskItemDto dto)
    {
        var common = EntityDtoRules.ValidateUpdate(dto);
        if (common.TryGetValue(out Error? commonError)) return commonError;

        var errors = new List<ValidationIssue>();
        if (string.IsNullOrWhiteSpace(dto.Title)) errors.Add(new ValidationIssue("Title is required.", nameof(TaskItemDto.Title), "title.required"));
        if (dto.Title?.Length > DomainConstants.RULE_DEFAULT_NAME_LENGTH_MAX) errors.Add(new ValidationIssue($"Title cannot exceed {DomainConstants.RULE_DEFAULT_NAME_LENGTH_MAX} characters.", nameof(TaskItemDto.Title), "title.max_length"));
        if (dto.Description?.Length > DomainConstants.RULE_DEFAULT_DESCRIPTION_LENGTH_MAX) errors.Add(new ValidationIssue($"Description cannot exceed {DomainConstants.RULE_DEFAULT_DESCRIPTION_LENGTH_MAX} characters.", nameof(TaskItemDto.Description), "description.max_length"));
        return errors.Count > 0 ? new ValidationError(errors) : dto;
    }
}
