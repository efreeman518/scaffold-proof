using EF.Domain.Contracts;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;

namespace Test.Support.Builders;

/// <summary>Builds category test data with sensible defaults so tests only override relevant fields.</summary>
public class CategoryBuilder
{
    private Guid _tenantId = TestConstants.TenantId;
    private string _name = "Test Category";
    private string? _description = "Test description";
    private int _sortOrder;
    private Guid? _parentCategoryId;

    /// <summary>Sets tenant ID on the builder so tests can override only scenario-specific values.</summary>
    public CategoryBuilder WithTenantId(Guid tenantId) { _tenantId = tenantId; return this; }
    /// <summary>Sets name on the builder so tests can override only scenario-specific values.</summary>
    public CategoryBuilder WithName(string name) { _name = name; return this; }
    /// <summary>Sets description on the builder so tests can override only scenario-specific values.</summary>
    public CategoryBuilder WithDescription(string? description) { _description = description; return this; }
    /// <summary>Sets sort order on the builder so tests can override only scenario-specific values.</summary>
    public CategoryBuilder WithSortOrder(int sortOrder) { _sortOrder = sortOrder; return this; }
    /// <summary>Sets parent category ID on the builder so tests can override only scenario-specific values.</summary>
    public CategoryBuilder WithParentCategoryId(Guid? parentCategoryId) { _parentCategoryId = parentCategoryId; return this; }

    /// <summary>Builds test data used by focused test cases.</summary>
    public Category Build()
    {
        var result = Category.Create(
            TenantId.From(_tenantId),
            _name,
            _description,
            _sortOrder,
            DomainId.FromNullable<CategoryId>(_parentCategoryId));
        return result.Value!;
    }
}
