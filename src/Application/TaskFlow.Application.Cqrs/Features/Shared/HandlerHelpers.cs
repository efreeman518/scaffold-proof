using EF.Common.Contracts;
using Microsoft.Extensions.Logging;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Models;
using TaskFlow.Application.Models.Paging;

namespace TaskFlow.Application.Cqrs.Shared;

/// <summary>
/// Small CQRS helpers that mirror service-layer response envelopes, cache keys, and tenant
/// search filtering so both application styles keep the same observable API behavior.
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

    /// <summary>Provides the cache key operation for handler helpers.</summary>
    public static string CacheKey(string entityName, Guid id) =>
        entityName + ":" + id;

    /// <summary>Provides the enforce tenant filter operation for handler helpers.</summary>
    public static void EnforceTenantFilter<TFilter>(
        SearchRequest<TFilter> request,
        Guid? requestTenantId,
        IReadOnlyCollection<string> roles,
        ILogger logger,
        string operation)
        where TFilter : DefaultSearchFilter, new()
    {
        if (roles.Contains(AppConstants.ROLE_GLOBAL_ADMIN))
        {
            return;
        }

        request.Filter ??= new TFilter();
        if (request.Filter.TenantId is Guid supplied && supplied != requestTenantId)
        {
            logger.LogTenantFilterManipulation(operation, requestTenantId, supplied);
        }

        request.Filter.TenantId = requestTenantId;
    }

    /// <summary>Cursor-request twin of <see cref="EnforceTenantFilter{TFilter}"/>.</summary>
    public static void EnforceCursorTenantFilter<TFilter, TSortMode>(
        CursorSearchRequest<TFilter, TSortMode> request,
        Guid? requestTenantId,
        IReadOnlyCollection<string> roles,
        ILogger logger,
        string operation)
        where TFilter : DefaultSearchFilter, new()
        where TSortMode : struct, Enum
    {
        if (roles.Contains(AppConstants.ROLE_GLOBAL_ADMIN))
        {
            return;
        }

        request.Filter ??= new TFilter();
        if (request.Filter.TenantId is Guid supplied && supplied != requestTenantId)
        {
            logger.LogTenantFilterManipulation(operation, requestTenantId, supplied);
        }

        request.Filter.TenantId = requestTenantId;
    }
}
