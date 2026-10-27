using EF.Common.Contracts;
using EF.Domain.Contracts;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Shared.Constants;

namespace TaskFlow.Application.Services.Rules;

/// <summary>Provides category structure validator behavior for the Application Rules layer.</summary>
internal static class CategoryStructureValidator
{
    /// <summary>Validates validate create rules and returns failures before work continues.</summary>
    public static Result<CategoryDto> ValidateCreate(CategoryDto dto)
    {
        var common = EntityDtoRules.ValidateCreate(dto);
        if (common.TryGetValue(out Error? commonError)) return commonError;

        var errors = new List<ValidationIssue>();
        if (string.IsNullOrWhiteSpace(dto.Name)) errors.Add(new ValidationIssue("Category name is required."));
        if (dto.Name?.Length > DomainConstants.RULE_CATEGORY_NAME_LENGTH_MAX) errors.Add(new ValidationIssue($"Category name cannot exceed {DomainConstants.RULE_CATEGORY_NAME_LENGTH_MAX} characters."));
        if (dto.Description?.Length > DomainConstants.RULE_CATEGORY_DESCRIPTION_LENGTH_MAX) errors.Add(new ValidationIssue($"Description cannot exceed {DomainConstants.RULE_CATEGORY_DESCRIPTION_LENGTH_MAX} characters."));
        return errors.Count > 0 ? new ValidationError(errors) : dto;
    }

    /// <summary>Validates validate update rules and returns failures before work continues.</summary>
    public static Result<CategoryDto> ValidateUpdate(CategoryDto dto)
    {
        var common = EntityDtoRules.ValidateUpdate(dto);
        if (common.TryGetValue(out Error? commonError)) return commonError;

        var errors = new List<ValidationIssue>();
        if (string.IsNullOrWhiteSpace(dto.Name)) errors.Add(new ValidationIssue("Category name is required."));
        if (dto.Name?.Length > DomainConstants.RULE_CATEGORY_NAME_LENGTH_MAX) errors.Add(new ValidationIssue($"Category name cannot exceed {DomainConstants.RULE_CATEGORY_NAME_LENGTH_MAX} characters."));
        if (dto.Description?.Length > DomainConstants.RULE_CATEGORY_DESCRIPTION_LENGTH_MAX) errors.Add(new ValidationIssue($"Description cannot exceed {DomainConstants.RULE_CATEGORY_DESCRIPTION_LENGTH_MAX} characters."));
        return errors.Count > 0 ? new ValidationError(errors) : dto;
    }
}
