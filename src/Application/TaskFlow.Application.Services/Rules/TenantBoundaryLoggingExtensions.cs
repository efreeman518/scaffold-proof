using Microsoft.Extensions.Logging;

namespace TaskFlow.Application.Services.Rules;

/// <summary>Provides tenant boundary logging extensions behavior for the Application Rules layer.</summary>
internal static partial class TenantBoundaryLoggingExtensions
{
    /// <summary>Provides the log validation failure operation for tenant boundary logging extensions.</summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Validation failure in {Context}. Messages={Messages}")]
    public static partial void LogValidationFailure(this ILogger logger, string context, string messages);

    /// <summary>Provides the log tenant filter manipulation operation for tenant boundary logging extensions.</summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Potential tenant filter manipulation in {Context}. RequestTenant={RequestTenantId} SuppliedTenant={SuppliedTenantId}")]
    public static partial void LogTenantFilterManipulation(this ILogger logger, string context, Guid? requestTenantId, Guid? suppliedTenantId);

    /// <summary>Provides the log tenant change attempt operation for tenant boundary logging extensions.</summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Attempted tenant change on {Entity} {EntityId}. ExistingTenant={ExistingTenantId} IncomingTenant={IncomingTenantId}")]
    public static partial void LogTenantChangeAttempt(this ILogger logger, string entity, Guid? entityId, Guid? existingTenantId, Guid? incomingTenantId);

    /// <summary>Logs a tenant boundary check that failed because the caller has no roles.</summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Tenant boundary violation attempt: Caller without roles attempted access. Operation={Operation}, Entity={EntityName}, EntityId={EntityId}")]
    public static partial void LogTenantBoundaryNoRoles(this ILogger logger, string operation, string entityName, Guid? entityId);

    /// <summary>Logs a tenant boundary check that failed because a non-GlobalAdmin caller targeted a global entity.</summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Tenant boundary violation attempt: Non-GlobalAdmin tried to access a global entity. Operation={Operation}, Entity={EntityName}, EntityId={EntityId}")]
    public static partial void LogTenantBoundaryGlobalEntity(this ILogger logger, string operation, string entityName, Guid? entityId);

    /// <summary>Logs a tenant boundary check that failed because the caller's tenant does not match the entity's tenant.</summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Tenant boundary violation attempt: CallerTenantId={CallerTenantId}, EntityTenantId={EntityTenantId}, Operation={Operation}, Entity={EntityName}, EntityId={EntityId}")]
    public static partial void LogTenantBoundaryMismatch(this ILogger logger, Guid? callerTenantId, Guid? entityTenantId, string operation, string entityName, Guid? entityId);
}
