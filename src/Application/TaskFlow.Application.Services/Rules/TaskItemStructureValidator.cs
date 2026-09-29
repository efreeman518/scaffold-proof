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
        if (common.IsFailure) return Result<TaskItemDto>.Failure(common.Errors);

        var errors = new List<DomainError>();
        if (string.IsNullOrWhiteSpace(dto.Title)) errors.Add(DomainError.Create("Title is required."));
        if (dto.Title?.Length > DomainConstants.RULE_DEFAULT_NAME_LENGTH_MAX) errors.Add(DomainError.Create($"Title cannot exceed {DomainConstants.RULE_DEFAULT_NAME_LENGTH_MAX} characters."));
        if (dto.Description?.Length > DomainConstants.RULE_DEFAULT_DESCRIPTION_LENGTH_MAX) errors.Add(DomainError.Create($"Description cannot exceed {DomainConstants.RULE_DEFAULT_DESCRIPTION_LENGTH_MAX} characters."));
        return errors.Count > 0 ? Result<TaskItemDto>.Failure(errors) : Result<TaskItemDto>.Success(dto);
    }

    /// <summary>Validates validate update rules and returns failures before work continues.</summary>
    public static Result<TaskItemDto> ValidateUpdate(TaskItemDto dto)
    {
        var common = EntityDtoRules.ValidateUpdate(dto);
        if (common.IsFailure) return Result<TaskItemDto>.Failure(common.Errors);

        var errors = new List<DomainError>();
        if (string.IsNullOrWhiteSpace(dto.Title)) errors.Add(DomainError.Create("Title is required."));
        if (dto.Title?.Length > DomainConstants.RULE_DEFAULT_NAME_LENGTH_MAX) errors.Add(DomainError.Create($"Title cannot exceed {DomainConstants.RULE_DEFAULT_NAME_LENGTH_MAX} characters."));
        if (dto.Description?.Length > DomainConstants.RULE_DEFAULT_DESCRIPTION_LENGTH_MAX) errors.Add(DomainError.Create($"Description cannot exceed {DomainConstants.RULE_DEFAULT_DESCRIPTION_LENGTH_MAX} characters."));
        return errors.Count > 0 ? Result<TaskItemDto>.Failure(errors) : Result<TaskItemDto>.Success(dto);
    }
}
