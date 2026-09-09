using EF.Common.Contracts;
using TaskFlow.Application.Contracts.Concurrency;
using TaskFlow.Application.Models.Shared;

namespace TaskFlow.Application.Services.Rules;

/// <summary>
/// Common structure validation rules for DTOs.
/// Matches Portal pattern: generic ValidateCreate/ValidateUpdate for any DTO
/// implementing IEntityBaseDto / ITenantEntityDto.
/// </summary>
public static class StructureValidators
{
    /// <summary>Provides the require operation for structure validators.</summary>
    internal static Result Require(bool condition, string errorMessage) =>
        condition ? Result.Success() : Result.Failure(errorMessage);

    /// <summary>
    /// Validates common create preconditions for tenant-scoped DTOs.
    /// Entity-specific validators should call this first, then add entity-specific checks.
    /// </summary>
    internal static Result ValidateCreate<T>(T? dto) where T : class, ITenantEntityDto
    {
        if (dto is null) return Result.Failure("Payload is required.");

        // GR-17: a caller may supply the create id, but only a UUIDv7 - a random v4 from a client
        // would fragment the clustered index every create lands in.
        var callerId = dto is IEntityBaseDto entityDto ? entityDto.Id : null;
        return Result.Combine(
            Require(dto.TenantId != Guid.Empty, "TenantId is required."),
            UuidV7.ValidateCallerId(callerId));
    }

    /// <summary>
    /// Validates common update preconditions for tenant-scoped DTOs with Id.
    /// </summary>
    internal static Result ValidateUpdate<T>(T? dto) where T : class, IEntityBaseDto, ITenantEntityDto
    {
        if (dto is null) return Result.Failure("Payload is required.");
        return Result.Combine(
            Require(dto.Id.HasValue && dto.Id.Value != Guid.Empty, "Id is required for update."),
            Require(dto.TenantId != Guid.Empty, "TenantId is required.")
        );
    }

    /// <summary>
    /// Validates that an Id is present for update operations (no tenant check).
    /// </summary>
    internal static Result ValidateUpdateId<T>(T? dto) where T : class, IEntityBaseDto
    {
        if (dto is null) return Result.Failure("Payload is required.");
        return Require(dto.Id.HasValue && dto.Id.Value != Guid.Empty, "Id is required for update.");
    }
}
