using EF.Common.Contracts;
using EF.Domain.Contracts;
using TaskFlow.Application.Cqrs.Shared;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Shared.Constants;

namespace TaskFlow.Application.Cqrs.Features.Tags;

/// <summary>Provides tag structure validator behavior for the Features Tags layer.</summary>
internal static class TagStructureValidator
{
    /// <summary>Validates validate create rules and returns failures before work continues.</summary>
    public static Result<TagDto> ValidateCreate(TagDto dto)
    {
        var common = EntityDtoRules.ValidateCreate(dto);
        if (common.IsFailure) return Result<TagDto>.Failure(common.Errors);

        var errors = new List<DomainError>();
        if (string.IsNullOrWhiteSpace(dto.Name)) errors.Add(DomainError.Create("Tag name is required."));
        if (dto.Name?.Length > DomainConstants.RULE_TAG_NAME_LENGTH_MAX) errors.Add(DomainError.Create($"Tag name cannot exceed {DomainConstants.RULE_TAG_NAME_LENGTH_MAX} characters."));
        if (dto.Color?.Length > DomainConstants.RULE_TAG_COLOR_LENGTH_MAX) errors.Add(DomainError.Create($"Color cannot exceed {DomainConstants.RULE_TAG_COLOR_LENGTH_MAX} characters."));
        return errors.Count > 0 ? Result<TagDto>.Failure(errors) : Result<TagDto>.Success(dto);
    }

    /// <summary>Validates validate update rules and returns failures before work continues.</summary>
    public static Result<TagDto> ValidateUpdate(TagDto dto)
    {
        var common = EntityDtoRules.ValidateUpdate(dto);
        if (common.IsFailure) return Result<TagDto>.Failure(common.Errors);

        var errors = new List<DomainError>();
        if (string.IsNullOrWhiteSpace(dto.Name)) errors.Add(DomainError.Create("Tag name is required."));
        if (dto.Name?.Length > DomainConstants.RULE_TAG_NAME_LENGTH_MAX) errors.Add(DomainError.Create($"Tag name cannot exceed {DomainConstants.RULE_TAG_NAME_LENGTH_MAX} characters."));
        if (dto.Color?.Length > DomainConstants.RULE_TAG_COLOR_LENGTH_MAX) errors.Add(DomainError.Create($"Color cannot exceed {DomainConstants.RULE_TAG_COLOR_LENGTH_MAX} characters."));
        return errors.Count > 0 ? Result<TagDto>.Failure(errors) : Result<TagDto>.Success(dto);
    }
}
