using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using TaskFlow.Application.Contracts;

namespace Test.Endpoints;

/// <summary>
/// D-075: a FlowEngine document node reads attachment content with a system read, so whoever may save and start a
/// workflow definition can name any attachment id. Workflow authoring (the <c>FlowEngine.Admin</c> policy on
/// <c>MapFlowEngineAdmin</c>) must therefore stay GlobalAdmin-only, a role that already reads every tenant. The policy
/// is checked as the API host composes it: TaskFlow's role policy and the package's scope-claim default share the name.
/// Endpoint tier: the real host's authorization services, synthetic principals, no requests.
/// </summary>
[TestClass]
public sealed class FlowEngineAdminPolicyTests
{
    private const string Policy = "FlowEngine.Admin";
    private const string Why =
        "D-075: evidence reads are system reads, so workflow authoring (FlowEngine.Admin) must require GlobalAdmin and nothing narrower.";

    [TestMethod]
    [TestCategory("Endpoint")]
    public async Task FlowEngineAdminPolicy_RequiresGlobalAdmin_AndNothingNarrower()
    {
        using var factory = new CustomApiFactory(EndpointStyles.Service);
        var authorization = factory.Services.GetRequiredService<IAuthorizationService>();

        Assert.IsTrue((await authorization.AuthorizeAsync(Principal(AppConstants.ROLE_GLOBAL_ADMIN), Policy)).Succeeded, Why);
        foreach (var role in new[] { AppConstants.ROLE_TENANT_ADMIN, AppConstants.ROLE_TENANT_MEMBER, AppConstants.ROLE_SYSTEM })
        {
            Assert.IsFalse((await authorization.AuthorizeAsync(Principal(role), Policy)).Succeeded, $"{role}: {Why}");
        }

        var scopeOnly = Principal(AppConstants.ROLE_TENANT_ADMIN, new Claim("scope", Policy));
        Assert.IsFalse((await authorization.AuthorizeAsync(scopeOnly, Policy)).Succeeded,
            $"a FlowEngine.Admin scope claim without the GlobalAdmin role: {Why}");
    }

    private static ClaimsPrincipal Principal(string role, params Claim[] extra) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "policy-test"), new Claim("tenant_id", ScaffoldPrincipal.TenantId), new Claim(ClaimTypes.Role, role), .. extra],
            authenticationType: "policy-test"));
}
