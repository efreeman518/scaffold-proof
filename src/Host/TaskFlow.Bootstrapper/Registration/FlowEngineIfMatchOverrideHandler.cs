using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;

namespace TaskFlow.Bootstrapper;

/// <summary>
/// D-032 trusted-automation override for the FlowEngine self-call client ("taskflow-api", wired in
/// <see cref="RegisterServices.AddTaskFlowConnectorClients"/>). GR-16 requires <c>If-Match</c> on
/// PATCH/PUT/DELETE (428 when missing), but EF.FlowEngine 1.0.163's "integration" node type
/// (<c>IntegrationNodeConfig</c>) has no way to send a custom header - unlike the "fetch" node type's
/// <c>FetchNodeConfig.Headers</c>. ai-task-triage.json's PATCH nodes already carry a forward-compatible
/// no-op <c>"headers": {"If-Match": "*"}</c> in their JSON config for when that lands; until then this
/// handler makes the override real at the transport level, scoped to this one named client.
/// <para>
/// This is not a silent fallback: the API still enforces the concurrency precondition end to end
/// (ConcurrencyGuard / 412 on a real conflict). "*" is the documented trusted-automation override for
/// workflow self-calls (D-032), and every use is logged here (client side) as well as by
/// IfMatchEndpointFilter (server side).
/// </para>
/// <para>
/// Removal criterion: EF.FlowEngine <c>IntegrationNodeConfig.Headers</c> ships and ai-task-triage.json's
/// PATCH nodes are verified to send <c>If-Match</c> through node config directly - see
/// <c>WorkflowDefinitionValidityTests.IntegrationNodeConfig_HasNoHeadersProperty_PackageRequest18</c> in
/// Test.Integration.FlowEngine, which fails the moment that guard trips.
/// </para>
/// </summary>
internal sealed class FlowEngineIfMatchOverrideHandler(ILogger<FlowEngineIfMatchOverrideHandler> logger)
    : DelegatingHandler
{
    private static readonly HashSet<HttpMethod> MutatingMethods = [HttpMethod.Patch, HttpMethod.Put, HttpMethod.Delete];

    /// <summary>Adds the wildcard If-Match override to mutating requests that carry no If-Match of their own.</summary>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (MutatingMethods.Contains(request.Method) && request.Headers.IfMatch.Count == 0)
        {
            request.Headers.IfMatch.Add(EntityTagHeaderValue.Any);

            var route = request.RequestUri?.IsAbsoluteUri == true
                ? request.RequestUri.PathAndQuery
                : request.RequestUri?.ToString() ?? string.Empty;
            logger.FlowEngineIfMatchOverrideApplied(request.Method.Method, route);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
