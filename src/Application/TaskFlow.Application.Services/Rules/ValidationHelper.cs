using EF.Common.Contracts;
using Microsoft.Extensions.Logging;
using TaskFlow.Application.Contracts;

namespace TaskFlow.Application.Services.Rules;

/// <summary>Provides validation helper behavior for the Application Rules layer.</summary>
internal static class ValidationHelper
{
    /// <summary>Validates ensure global admin rules and returns failures before work continues.</summary>
    public static Result EnsureGlobalAdmin(IReadOnlyCollection<string> callerRoles, string operation)
    {
        return callerRoles.Contains(AppConstants.ROLE_GLOBAL_ADMIN)
            ? Result.Success()
            : Result.Failure($"Forbidden: Only a GlobalAdmin may perform this operation: {operation}.");
    }

    /// <summary>Validates ensure tenant boundary rules and returns failures before work continues.</summary>
    public static Result EnsureTenantBoundary(
        ILogger logger,
        Guid? callerTenantId,
        IReadOnlyCollection<string> callerRoles,
        Guid? entityTenantId,
        string operation,
        string entityName,
        Guid? entityId = null)
    {
        if (callerRoles.Contains(AppConstants.ROLE_GLOBAL_ADMIN))
            return Result.Success();

        if (callerRoles is null || callerRoles.Count == 0)
        {
            logger.LogTenantBoundaryNoRoles(operation, entityName, entityId);
            return Result.Failure($"Forbidden: Tenant boundary violation for operation: {operation}.");
        }

        if (entityTenantId is null)
        {
            logger.LogTenantBoundaryGlobalEntity(operation, entityName, entityId);
            return Result.Failure($"Forbidden: Tenant boundary violation for operation: {operation}.");
        }

        if (callerTenantId.HasValue && callerTenantId.Value == entityTenantId)
            return Result.Success();

        logger.LogTenantBoundaryMismatch(callerTenantId, entityTenantId, operation, entityName, entityId);

        return Result.Failure($"Forbidden: Tenant boundary violation for operation: {operation}.");
    }

    /// <summary>Provides the prevent tenant change operation for validation helper.</summary>
    public static Result PreventTenantChange(ILogger logger, Guid? existingTenantId, Guid? incomingTenantId, string entityName, Guid entityId)
    {
        if (existingTenantId != incomingTenantId)
        {
            logger.LogTenantChangeAttempt(entityName, entityId, existingTenantId, incomingTenantId);
            return Result.Failure($"TenantId cannot be changed for an existing {entityName}.");
        }
        return Result.Success();
    }
}
