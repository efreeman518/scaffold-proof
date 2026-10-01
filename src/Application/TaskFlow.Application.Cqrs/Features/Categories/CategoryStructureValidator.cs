using EF.Common.Contracts;
using EF.Domain.Contracts;
using TaskFlow.Application.Cqrs.Shared;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Shared.Constants;

namespace TaskFlow.Application.Cqrs.Features.Categories;

/// <summary>Provides category structure validator behavior for the Features Categories layer.</summary>
internal static class CategoryStructureValidator
{
    /// <summary>Validates validate create rules and returns failures before work continues.</summary>
    public static Result<CategoryDto> ValidateCreate(CategoryDto dto)
    {
        var common = EntityDtoRules.ValidateCreate(dto);
        if (common.IsFailure) return Result<CategoryDto>.Failure(common.Errors);

        var errors = new List<DomainError>();
        if (string.IsNullOrWhiteSpace(dto.Name)) errors.Add(DomainError.Create("Category name is required."));
        if (dto.Name?.Length > DomainConstants.RULE_CATEGORY_NAME_LENGTH_MAX) errors.Add(DomainError.Create($"Category name cannot exceed {DomainConstants.RULE_CATEGORY_NAME_LENGTH_MAX} characters."));
        if (dto.Description?.Length > DomainConstants.RULE_CATEGORY_DESCRIPTION_LENGTH_MAX) errors.Add(DomainError.Create($"Description cannot exceed {DomainConstants.RULE_CATEGORY_DESCRIPTION_LENGTH_MAX} characters."));
        return errors.Count > 0 ? Result<CategoryDto>.Failure(errors) : Result<CategoryDto>.Success(dto);
    }

    /// <summary>Validates validate update rules and returns failures before work continues.</summary>
    public static Result<CategoryDto> ValidateUpdate(CategoryDto dto)
    {
        var common = EntityDtoRules.ValidateUpdate(dto);
        if (common.IsFailure) return Result<CategoryDto>.Failure(common.Errors);

        var errors = new List<DomainError>();
        if (string.IsNullOrWhiteSpace(dto.Name)) errors.Add(DomainError.Create("Category name is required."));
        if (dto.Name?.Length > DomainConstants.RULE_CATEGORY_NAME_LENGTH_MAX) errors.Add(DomainError.Create($"Category name cannot exceed {DomainConstants.RULE_CATEGORY_NAME_LENGTH_MAX} characters."));
        if (dto.Description?.Length > DomainConstants.RULE_CATEGORY_DESCRIPTION_LENGTH_MAX) errors.Add(DomainError.Create($"Description cannot exceed {DomainConstants.RULE_CATEGORY_DESCRIPTION_LENGTH_MAX} characters."));
        return errors.Count > 0 ? Result<CategoryDto>.Failure(errors) : Result<CategoryDto>.Success(dto);
    }
}
