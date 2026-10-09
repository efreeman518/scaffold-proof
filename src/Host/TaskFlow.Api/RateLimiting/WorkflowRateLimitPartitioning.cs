using System.Threading.RateLimiting;
using EF.Auth.Relay;
using EF.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace TaskFlow.Api.RateLimiting;

/// <summary>
/// Gives workflow self-calls their own partition and allowance in the tenant limiter, so workflow traffic neither
/// starves nor is starved by the tenant's interactive traffic. EF.RateLimiting partitions on the tenant claim
/// alone, with no caller hook, so this replaces the global limiter it installs with one that asks the same
/// <see cref="TenantRateLimitPartitioner"/>: a request whose principal the relay built from a trusted caller's header
/// (<see cref="Auth.RelayedPrincipalMarker"/>), relayed by a caller in both <c>ForwardedClaims:TrustedCallerIds</c> and
/// <see cref="WorkflowRateLimitSettings.CallerIds"/>, that the default budget would meter by its tenant gets
/// the tenant's <see cref="WorkflowRateLimitSettings.Budget"/> budget partition (storage key
/// <c>{prefix}:{namespace}:workflow:tenant:{id}</c>, shared through Redis like every other budget); every other request,
/// a Gateway-relayed user included, keeps the package's partition. Exempt paths and endpoints with their own budget are
/// unchanged. A token that carries a <c>ForwardedClaims:RelayedByClaimType</c> claim of its own, issued by an identity
/// provider rather than built by the relay, never reaches the workflow budget. A rejection is still reported by the
/// package under the default budget's telemetry tag. With no caller id configured (the shipped settings) the package's
/// limiter is left in place; with caller ids set, a disabled global limiter fails host start.
/// </summary>
internal sealed class WorkflowRateLimitPartitioning(
    TenantRateLimitPartitioner partitioner,
    IOptionsMonitor<TenantRateLimitSettings> tenantSettings,
    IOptions<WorkflowRateLimitSettings> workflow,
    IOptions<ForwardedClaimsOptions> relay) : IPostConfigureOptions<RateLimiterOptions>
{
    /// <summary>The package's tenant partition key prefix (<c>tenant:{id}</c>, documented on the partitioner).</summary>
    private const string TenantPartitionPrefix = "tenant:";

    public void PostConfigure(string? name, RateLimiterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var callerIds = workflow.Value.CallerIds;
        if (callerIds.Count == 0)
            return;

        if (options.GlobalLimiter is null)
        {
            throw new InvalidOperationException(
                $"{WorkflowRateLimitSettings.ConfigSectionName}:CallerIds is set, so RateLimiting:Tenants:UseGlobalLimiter " +
                "must stay true: the workflow budget replaces a partition of the global limiter.");
        }

        if (!tenantSettings.CurrentValue.Budgets.ContainsKey(WorkflowRateLimitSettings.Budget))
        {
            throw new InvalidOperationException(
                $"{WorkflowRateLimitSettings.ConfigSectionName}:CallerIds is set, so RateLimiting:Tenants:Budgets needs a " +
                $"'{WorkflowRateLimitSettings.Budget}' budget.");
        }

        var untrusted = callerIds.Where(id => !relay.Value.TrustedCallerIds.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();
        if (untrusted.Count > 0)
        {
            throw new InvalidOperationException(
                $"{WorkflowRateLimitSettings.ConfigSectionName}:CallerIds lists {string.Join(", ", untrusted)}, which " +
                "ForwardedClaims:TrustedCallerIds does not, so no request is ever relayed by it.");
        }

        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(Partition);
    }

    /// <summary>The global limiter's partition for <paramref name="context"/>.</summary>
    internal RateLimitPartition<string> Partition(HttpContext context)
    {
        var partition = partitioner.Default(context);
        if (!IsWorkflowCall(context) || !partition.PartitionKey.StartsWith(TenantPartitionPrefix, StringComparison.Ordinal))
            return partition;

        // The budget partition carries the same "tenant:{id}" key as the default one; one partitioned limiter holds
        // both, so the key is prefixed or the first limiter created for the tenant would serve both.
        var budget = partitioner.Budget(context, WorkflowRateLimitSettings.Budget);
        return RateLimitPartition.Get($"{WorkflowRateLimitSettings.Budget}:{budget.PartitionKey}", budget.Factory);
    }

    private bool IsWorkflowCall(HttpContext context) =>
        Auth.RelayedPrincipalMarker.IsRelayed(context)
        && context.User.FindFirst(relay.Value.RelayedByClaimType)?.Value is { } caller
        && relay.Value.TrustedCallerIds.Contains(caller, StringComparer.OrdinalIgnoreCase)
        && workflow.Value.CallerIds.Contains(caller, StringComparer.OrdinalIgnoreCase);
}
