using EF.Common.Contracts;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Contracts.Caching;
using TaskFlow.Application.Models;

namespace TaskFlow.Application.Cqrs.Shared;

/// <summary>
/// Small CQRS helpers that mirror service-layer response envelopes and cache keys so both application
/// styles keep the same observable API behavior. Tenant search filtering is EF.Tenancy's EnforceTenantFilter.
/// </summary>
internal static class HandlerHelpers
{
    /// <summary>Builds response from current configuration and inputs.</summary>
    public static DefaultResponse<TDto> BuildResponse<TDto>(TDto? dto) =>
        new() { Item = dto, TenantInfo = null };

    /// <summary>Provides the success operation for handler helpers.</summary>
    public static Result<DefaultResponse<TDto>> Success<TDto>(TDto? dto) =>
        Result<DefaultResponse<TDto>>.Success(BuildResponse(dto));

    /// <summary>Success envelope for a child of the TaskItem aggregate, tagged with the root version (D-031).</summary>
    public static Result<DefaultResponse<TDto>> SuccessForChild<TDto>(TDto? dto, long aggregateVersion) =>
        Result<DefaultResponse<TDto>>.Success(
            new DefaultResponse<TDto> { Item = dto, TenantInfo = null, AggregateVersion = aggregateVersion });

    /// <summary>Provides the not found response operation for handler helpers.</summary>
    public static Result<DefaultResponse<TDto>> NotFoundResponse<TDto>() =>
        Success<TDto>(default);

    /// <summary>
    /// The cache tag a successful write to one entity type invalidates. Handlers evict by what they changed;
    /// which cached snapshots carry that tag is the cache's business, not the handler's.
    /// </summary>
    public static string EntityTag(Guid? tenantId, string entityName) =>
        CacheTags.Entity(tenantId ?? Guid.Empty, entityName.ToLowerInvariant());
}
