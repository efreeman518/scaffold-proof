using Microsoft.Extensions.Logging;

namespace TaskFlow.Application.Cqrs.Shared;

/// <summary>Provides tenant boundary logging extensions behavior for the Features Shared layer.</summary>
internal static partial class TenantBoundaryLoggingExtensions
{
    /// <summary>Provides the log tenant filter manipulation operation for tenant boundary logging extensions.</summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Potential tenant filter manipulation in {Context}. RequestTenant={RequestTenantId} SuppliedTenant={SuppliedTenantId}")]
    public static partial void LogTenantFilterManipulation(this ILogger logger, string context, Guid? requestTenantId, Guid? suppliedTenantId);
}
