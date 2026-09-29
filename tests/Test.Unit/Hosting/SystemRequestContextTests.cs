using EF.Cache;
using EF.Common.Contracts;
using EF.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Security.Claims;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.Models;
using TaskFlow.Application.Services;
using TaskFlow.Bootstrapper;
using TaskFlow.Domain.Model;
using Test.Support.Builders;

namespace Test.Unit.Hosting;

/// <summary>
/// The request context outside an HTTP request is the package system context (no tenant, the system user, the
/// system role), never the scaffold admin; an unauthenticated HTTP request is anonymous (EF.AspNetCore 2.0 invents no
/// identity for it). Background writes that relied on the scaffold admin's GlobalAdmin bypass - the AI reviewer
/// loading a task and adding a comment through the tenant boundary - keep working because the system role is one of
/// EF.Tenancy's cross-tenant roles (D-067), and a token cannot claim the system role.
/// Pure-unit tier: the real registrations and the real tenant-boundary validator; repositories are mocked.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SystemRequestContextTests
{
    private static readonly Guid SomeTenant = Guid.Parse("0197a000-0000-7000-8000-00000000abcd");

    /// <summary>MSTest-injected context; supplies the per-test cancellation token.</summary>
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void NoHttpRequest_ResolvesTheExplicitSystemIdentity()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<IRequestContext<string, Guid?>>();

        Assert.AreEqual(AppConstants.SYSTEM_USER_ID, context.AuditId);
        Assert.IsNull(context.TenantId, "background work must not be pinned to the scaffold tenant");
        CollectionAssert.AreEqual(new[] { AppConstants.ROLE_SYSTEM }, context.Roles.ToArray());
    }

    [TestMethod]
    public async Task NoHttpRequest_TenantTargetingTargetsNoTenant()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var targeting = await new TenantTargetingContextAccessor(scope.ServiceProvider).GetContextAsync();

        Assert.AreEqual(string.Empty, targeting.UserId);
    }

    [TestMethod]
    public void HttpRequestWithoutUser_ResolvesAnAnonymousContext()
    {
        using var provider = BuildProvider();
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext();
        using var scope = provider.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<IRequestContext<string, Guid?>>();

        Assert.AreEqual("anonymous", context.AuditId);
        Assert.IsNull(context.TenantId, "an unauthenticated request must not be given the scaffold tenant");
        Assert.IsEmpty(context.Roles);
    }

    [TestMethod]
    public void AuthenticatedCaller_ClaimingTheSystemRole_DoesNotGetIt()
    {
        using var provider = BuildProvider();
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("oid", "user-1"),
                new Claim("tenant_id", SomeTenant.ToString()),
                new Claim(ClaimTypes.Role, AppConstants.ROLE_TENANT_MEMBER),
                new Claim(ClaimTypes.Role, "system")
            ], "Test"))
        };
        using var scope = provider.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<IRequestContext<string, Guid?>>();

        CollectionAssert.AreEqual(new[] { AppConstants.ROLE_TENANT_MEMBER }, context.Roles.ToArray());
    }

    /// <summary>Only the system role is stripped from a token: a real global-admin claim keeps its role and tenant.</summary>
    [TestMethod]
    public void AuthenticatedGlobalAdmin_KeepsTheGlobalAdminRole()
    {
        using var provider = BuildProvider();
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("oid", "admin-1"),
                new Claim("tenant_id", SomeTenant.ToString()),
                new Claim(ClaimTypes.Role, AppConstants.ROLE_GLOBAL_ADMIN)
            ], "Test"))
        };
        using var scope = provider.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<IRequestContext<string, Guid?>>();

        Assert.AreEqual("admin-1", context.AuditId);
        Assert.AreEqual(SomeTenant, context.TenantId);
        CollectionAssert.AreEqual(new[] { AppConstants.ROLE_GLOBAL_ADMIN }, context.Roles.ToArray());
    }

    [TestMethod]
    public void TenantBoundary_SystemIdentity_ActsForTheTenantTheDataNames()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var systemContext = scope.ServiceProvider.GetRequiredService<IRequestContext<string, Guid?>>();
        var validator = scope.ServiceProvider.GetRequiredService<ITenantBoundaryValidator>();

        var system = validator.EnsureTenantBoundary(systemContext.TenantId, systemContext.Roles,
            SomeTenant, "TaskItem:Get", nameof(TaskItem));
        var tenantlessMember = validator.EnsureTenantBoundary(null, [AppConstants.ROLE_TENANT_MEMBER],
            SomeTenant, "TaskItem:Get", nameof(TaskItem));

        Assert.IsTrue(system.IsSuccess, "the system role is one of the cross-tenant roles EF.Tenancy lets through");
        Assert.IsTrue(tenantlessMember.IsFailure, "only the system identity is exempt; a tenantless caller is not");
    }

    /// <summary>
    /// The AI reviewer's write path (load the root through the boundary, add a comment, save) with the context a
    /// background host now resolves: it must still save, for a task in any tenant.
    /// </summary>
    [TestMethod]
    public async Task BackgroundCommentWrite_WithSystemIdentity_PassesTheBoundaryAndSaves()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var systemContext = scope.ServiceProvider.GetRequiredService<IRequestContext<string, Guid?>>();
        var task = new TaskItemBuilder().WithTenantId(SomeTenant).Build();
        var repoTrxn = new Mock<ITaskItemRepositoryTrxn>();
        repoTrxn.Setup(r => r.GetTaskItemAsync(task.Id, false, It.IsAny<CancellationToken>())).ReturnsAsync(task);
        var service = new TaskItemService(
            NullLogger<TaskItemService>.Instance,
            systemContext,
            repoTrxn.Object,
            Mock.Of<ITaskItemRepositoryQuery>(),
            scope.ServiceProvider.GetRequiredService<ITenantBoundaryValidator>(),
            Mock.Of<ITypedCache>());

        var result = await service.AddCommentAsync(task.Id.Value, new CommentDto { Body = "AI readiness review" },
            TestContext.CancellationToken);

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        Assert.IsNotNull(result.Value!.Item);
        repoTrxn.Verify(r => r.SaveChangesAsync(It.IsAny<EF.Data.Contracts.OptimisticConcurrencyWinner>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        RegisterServices.AddRequestContext(services);
        RegisterServices.AddTenantBoundary(services);
        return services.BuildServiceProvider();
    }
}
