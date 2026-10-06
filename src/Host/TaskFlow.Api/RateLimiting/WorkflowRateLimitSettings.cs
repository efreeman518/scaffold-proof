namespace TaskFlow.Api.RateLimiting;

/// <summary>
/// Which relayed callers are workflow self-calls (section <see cref="ConfigSectionName"/>). A request relayed (D-068)
/// by a caller listed in <see cref="CallerIds"/> is metered by the per-tenant <see cref="Budget"/> budget of
/// <c>RateLimiting:Tenants:Budgets</c> instead of the tenant's tier (<see cref="WorkflowRateLimitPartitioning"/>).
/// Empty (the shipped value) changes nothing.
/// </summary>
internal sealed class WorkflowRateLimitSettings
{
    public const string ConfigSectionName = "RateLimiting:Workflow";

    /// <summary>The <c>RateLimiting:Tenants:Budgets</c> entry that meters workflow self-calls.</summary>
    public const string Budget = "workflow";

    /// <summary>
    /// The app-only client ids of the workflow hosts (Api, Scheduler, Functions), each also listed in
    /// <c>ForwardedClaims:TrustedCallerIds</c>; compared case-insensitively, as the relay compares them.
    /// </summary>
    public List<string> CallerIds { get; set; } = [];
}
