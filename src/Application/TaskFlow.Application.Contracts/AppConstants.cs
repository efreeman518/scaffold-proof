namespace TaskFlow.Application.Contracts;

/// <summary>Provides app constants behavior for the Application layer.</summary>
public static class AppConstants
{
    public const string DEFAULT_TIMEZONE = "America/New_York";
    public const int CACHE_PROVIDER_DEFAULT_DURATION_SECONDS = 60 * 60; // 1 hour
    public const string ROLE_GLOBAL_ADMIN = "GlobalAdmin";
    public const string ROLE_TENANT_ADMIN = "TenantAdmin";
    public const string ROLE_TENANT_MEMBER = "TenantMember";

    /// <summary>
    /// Role of the explicit system identity every host resolves outside an HTTP request (message consumers,
    /// scheduled jobs, the AI reviewer). It has no tenant of its own and acts for the tenant the data names;
    /// it can never come from a caller's token (the request-context factory strips it from claims).
    /// </summary>
    public const string ROLE_SYSTEM = "System";

    /// <summary>User id (and audit id) of the system identity.</summary>
    public const string SYSTEM_USER_ID = "system";
    public const string DEFAULT_CACHE = "TaskFlowCache";
    public const string DEFAULT_DATETIME_FORMAT = "yyyy-MM-ddTHH:mm";
}
