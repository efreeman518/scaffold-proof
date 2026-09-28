using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;

namespace Test.Support.Builders;

/// <summary>Builds checklist item test data with sensible defaults so tests only override relevant fields.</summary>
public class ChecklistItemBuilder
{
    private Guid _tenantId = TestConstants.TenantId;
    private Guid _taskItemId = Guid.NewGuid();
    private string _title = "Test Checklist Item";
    private int _sortOrder;

    /// <summary>Sets tenant ID on the builder so tests can override only scenario-specific values.</summary>
    public ChecklistItemBuilder WithTenantId(Guid tenantId) { _tenantId = tenantId; return this; }
    /// <summary>Sets task item ID on the builder so tests can override only scenario-specific values.</summary>
    public ChecklistItemBuilder WithTaskItemId(Guid taskItemId) { _taskItemId = taskItemId; return this; }
    /// <summary>Sets title on the builder so tests can override only scenario-specific values.</summary>
    public ChecklistItemBuilder WithTitle(string title) { _title = title; return this; }
    /// <summary>Sets sort order on the builder so tests can override only scenario-specific values.</summary>
    public ChecklistItemBuilder WithSortOrder(int sortOrder) { _sortOrder = sortOrder; return this; }

    /// <summary>Builds test data used by focused test cases.</summary>
    public ChecklistItem Build()
    {
        var result = ChecklistItem.Create(
            TenantId.From(_tenantId),
            TaskItemId.From(_taskItemId),
            _title,
            _sortOrder);
        return result.Value!;
    }
}
