using EF.Common.Contracts;
using Microsoft.Extensions.Logging;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Services.Rules;

namespace TaskFlow.Application.Services;

/// <summary>
/// Adapter around shared validation helpers so services and CQRS handlers depend on one
/// injectable tenant-boundary abstraction instead of static rule calls.
/// </summary>
internal sealed class TenantBoundaryValidator : ITenantBoundaryValidator
{
    /// <summary>Validates ensure tenant boundary rules and returns failures before work continues.</summary>
    public Result EnsureTenantBoundary(ILogger logger, Guid? requestTenantId,
        IReadOnlyCollection<string> roles, Guid? entityTenantId,
        string operation, string entityName, Guid? entityId = null)
    {
        return ValidationHelper.EnsureTenantBoundary(
            logger, requestTenantId, roles, entityTenantId,
            operation, entityName, entityId);
    }

    /// <summary>Validates ensure global admin rules and returns failures before work continues.</summary>
    public Result EnsureGlobalAdmin(IReadOnlyCollection<string> callerRoles, string operation)
    {
        return ValidationHelper.EnsureGlobalAdmin(callerRoles, operation);
    }

    /// <summary>Provides the prevent tenant change operation for tenant boundary validator.</summary>
    public Result PreventTenantChange(ILogger logger, Guid? currentTenantId, Guid? newTenantId,
        string entityName, Guid entityId)
    {
        return ValidationHelper.PreventTenantChange(logger, currentTenantId, newTenantId,
            entityName, entityId);
    }
}
