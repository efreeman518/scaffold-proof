using System.Security.Claims;

namespace TaskFlow.Application.Contracts;

/// <summary>
/// The fixed identity every request authenticates as in <see cref="AuthMode.Scaffold"/>, shared by the Api and the
/// Gateway so both hosts register the same principal (EF.Auth <c>AddFixedPrincipal</c>).
/// </summary>
public static class ScaffoldPrincipal
{
    public const string SchemeName = "Scaffold";
    public const string UserId = "scaffold-user";
    public const string TenantId = "00000000-0000-0000-0000-000000000001";
    public const string Name = "Scaffold Principal";

    /// <summary>
    /// Environments the fixed identity may run in. The reference app is login-free by design and deploys as
    /// <c>Production</c> (compose and Bicep), so Production is listed explicitly; any other environment fails host
    /// start. Kept in code rather than configuration so a setting can never widen it.
    /// </summary>
    public static IReadOnlyList<string> AllowedEnvironments { get; } = ["Development", "Testing", "Production"];

    /// <summary>The claims of the fixed identity, as (type, value) pairs.</summary>
    public static IReadOnlyList<(string Type, string Value)> Claims { get; } =
    [
        ("oid", UserId),
        (ClaimTypes.NameIdentifier, UserId),
        (ClaimTypes.Name, Name),
        ("tenant_id", TenantId),
        (ClaimTypes.Role, AppConstants.ROLE_GLOBAL_ADMIN),
        (ClaimTypes.Role, AppConstants.ROLE_TENANT_ADMIN),
        (ClaimTypes.Role, AppConstants.ROLE_TENANT_MEMBER)
    ];
}
