using TaskFlow.Application.Contracts;

namespace TaskFlow.Bootstrapper;

/// <summary>
/// The identity of the workflow self-calls (the FlowEngine <c>taskflow-api</c> client, section
/// <see cref="ConfigSectionName"/>). With <see cref="TokenScope"/> set, every self-call carries an app-only token for that
/// scope and the D-068 relay header naming the executing instance's tenant (<see cref="SelfCallRelayHandler"/>), so the
/// Api acts for that tenant. Empty (the shipped value), the self-calls carry neither and run as the Api's own
/// authentication: the scaffold principal (D-002).
/// </summary>
public sealed class SelfCallRelayOptions
{
    public const string ConfigSectionName = "FlowEngine:SelfCall";

    /// <summary>
    /// The named <c>EF.Auth</c> <c>ForwardedClaimsOptions</c> the self-call binds from the shared <c>ForwardedClaims</c>
    /// section. Named so the Api's own default instance, bound by its relay transformation, is not bound a second time.
    /// </summary>
    public const string ForwardedClaimsOptionsName = "FlowEngineSelfCall";

    /// <summary>The tenant the self-calls act for when no relay is configured: the scaffold principal's.</summary>
    public static readonly Guid ScaffoldTenantId = Guid.Parse(ScaffoldPrincipal.TenantId);

    /// <summary>The scope of the app-only token the self-calls carry (for example <c>api://taskflow-api/.default</c>).</summary>
    public string? TokenScope { get; set; }

    /// <summary>
    /// The self-call base address (<c>FlowEngine:TaskFlowApiBaseUrl</c>), the one address the relayed token is sent to.
    /// Set by the client registration, not bound from this section.
    /// </summary>
    internal Uri? ApiBaseAddress { get; set; }

    /// <summary>Whether the self-calls relay the instance tenant.</summary>
    public bool IsRelayConfigured => !string.IsNullOrWhiteSpace(TokenScope);

    /// <summary>
    /// Whether the self-call identity can act for <paramref name="tenantId"/>: any tenant when the relay is configured,
    /// otherwise only <see cref="ScaffoldTenantId"/>.
    /// </summary>
    public bool CanActFor(Guid tenantId) => IsRelayConfigured || tenantId == ScaffoldTenantId;
}
