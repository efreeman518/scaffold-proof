using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;

namespace Test.Support.Builders;

/// <summary>Builds tag test data with sensible defaults so tests only override relevant fields.</summary>
public class TagBuilder
{
    private Guid _tenantId = TestConstants.TenantId;
    private string _name = "Test Tag";
    private string? _color = "#FF0000";

    /// <summary>Sets tenant ID on the builder so tests can override only scenario-specific values.</summary>
    public TagBuilder WithTenantId(Guid tenantId) { _tenantId = tenantId; return this; }
    /// <summary>Sets name on the builder so tests can override only scenario-specific values.</summary>
    public TagBuilder WithName(string name) { _name = name; return this; }
    /// <summary>Sets color on the builder so tests can override only scenario-specific values.</summary>
    public TagBuilder WithColor(string? color) { _color = color; return this; }

    /// <summary>Builds test data used by focused test cases.</summary>
    public Tag Build()
    {
        var result = Tag.Create(TenantId.From(_tenantId), _name, _color);
        return result.Value!;
    }
}
