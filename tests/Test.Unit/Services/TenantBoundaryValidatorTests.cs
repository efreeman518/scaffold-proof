using Microsoft.Extensions.Logging.Abstractions;
using TaskFlow.Application.Services;
using Test.Support;

namespace Test.Unit.Services;

/// <summary>
/// The tenant-boundary check both application styles call through <c>ITenantBoundaryValidator</c>.
/// Pure-unit tier: a pure rule over its arguments.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class TenantBoundaryValidatorTests
{
    /// <summary>
    /// Verifies a caller with no role set fails closed. The role lookup used to run before the null check,
    /// so a null set threw instead of reaching the no-roles branch.
    /// </summary>
    [TestMethod]
    public void Given_NullRoles_When_EnsureTenantBoundary_Then_FailsClosed()
    {
        var result = new TenantBoundaryValidator().EnsureTenantBoundary(
            NullLogger.Instance, TestConstants.TenantId, null!, TestConstants.TenantId, "Test:Get", "TaskItem");

        Assert.IsTrue(result.IsFailure);
        StringAssert.StartsWith(result.ErrorMessage, "Forbidden:");
    }

    /// <summary>Verifies an empty role set fails closed even when the tenants match.</summary>
    [TestMethod]
    public void Given_EmptyRoles_When_EnsureTenantBoundary_Then_FailsClosed()
    {
        var result = new TenantBoundaryValidator().EnsureTenantBoundary(
            NullLogger.Instance, TestConstants.TenantId, [], TestConstants.TenantId, "Test:Get", "TaskItem");

        Assert.IsTrue(result.IsFailure);
    }
}
