using EF.Tenancy;

namespace TaskFlow.Application.Models;

/// <summary>
/// Base search filter. <see cref="TenantId"/> is the tenant predicate EF.Tenancy's
/// <c>EnforceTenantFilter</c> forces to the caller's tenant for every non-admin search.
/// </summary>
public record DefaultSearchFilter : ITenantScopedFilter
{
    public string? SearchTerm { get; set; }
    public Guid? TenantId { get; set; }
}
