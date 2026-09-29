using EF.Common.Contracts;
using TaskFlow.Application.Contracts;
using TaskFlow.Bootstrapper;

namespace Test.Unit.Hosting;

/// <summary>
/// The EF.Data tenant query filter fails closed, so which callers read across tenants is decided by
/// <see cref="RegisterServices.AllowsAllTenants"/> alone: a tenant-less system identity or global admin reads
/// every tenant, a caller with a tenant stays pinned to it, and any other tenant-less caller reads nothing.
/// Pure-unit tier: the rule only; no DI, no database.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class AllTenantsScopeTests
{
    private static readonly Guid SomeTenant = Guid.Parse("0197a000-0000-7000-8000-00000000abcd");

    [TestMethod]
    [DataRow(null, AppConstants.ROLE_SYSTEM, true, DisplayName = "system identity, no tenant")]
    [DataRow(null, AppConstants.ROLE_GLOBAL_ADMIN, true, DisplayName = "global admin, no tenant")]
    [DataRow(null, AppConstants.ROLE_TENANT_MEMBER, false, DisplayName = "member, no tenant")]
    [DataRow(null, null, false, DisplayName = "no roles, no tenant")]
    [DataRow("tenant", AppConstants.ROLE_GLOBAL_ADMIN, false, DisplayName = "global admin with a tenant")]
    [DataRow("tenant", AppConstants.ROLE_SYSTEM, false, DisplayName = "system role with a tenant")]
    public void AllowsAllTenants_FollowsTenantAndRole(string? tenant, string? role, bool expected)
    {
        var context = new RequestContext<string, Guid?>(
            Guid.NewGuid().ToString(), "caller", tenant is null ? null : SomeTenant, role is null ? [] : [role]);

        Assert.AreEqual(expected, RegisterServices.AllowsAllTenants(context));
    }
}
