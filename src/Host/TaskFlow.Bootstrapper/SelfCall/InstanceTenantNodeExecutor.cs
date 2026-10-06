using EF.FlowEngine.Abstractions;
using EF.FlowEngine.Definition;
using EF.FlowEngine.Model;

namespace TaskFlow.Bootstrapper;

/// <summary>
/// Runs a FlowEngine node executor with the executing instance's tenant (<see cref="ExecutionInstance.TenantId"/>) in
/// <see cref="CurrentTenant"/> for the duration of the node, so <see cref="SelfCallRelayHandler"/> on the HTTP client
/// the node calls relays that tenant. EF.FlowEngine 1.0.207 keeps its own ambient tenant internal, and the instance a
/// node executor receives is the one tenant source the engine carries through starts, resumes and child workflows
/// (D-075). The scope is an <see cref="AsyncLocal{T}"/>, so parallel children each see their own instance's tenant.
/// </summary>
internal sealed class InstanceTenantNodeExecutor(INodeExecutor inner) : INodeExecutor
{
    private static readonly AsyncLocal<string?> Tenant = new();

    /// <summary>The tenant of the instance whose node is running in this async flow, or null outside one.</summary>
    public static string? CurrentTenant => Tenant.Value;

    public string NodeType => inner.NodeType;

    public async Task<NodeResult> ExecuteAsync(
        NodeDefinition node, ExecutionInstance instance, IExecutionContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var previous = Tenant.Value;
        Tenant.Value = instance.TenantId;
        try
        {
            return await inner.ExecuteAsync(node, instance, ctx, ct).ConfigureAwait(false);
        }
        finally
        {
            Tenant.Value = previous;
        }
    }
}
