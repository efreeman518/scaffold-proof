using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;

namespace Test.Support.Builders;

/// <summary>Builds task item tag test data with sensible defaults so tests only override relevant fields.</summary>
public class TaskItemTagBuilder
{
    private Guid _tenantId = TestConstants.TenantId;
    private Guid _taskItemId = Guid.NewGuid();
    private Guid _tagId = Guid.NewGuid();

    /// <summary>Sets tenant ID on the builder so tests can override only scenario-specific values.</summary>
    public TaskItemTagBuilder WithTenantId(Guid tenantId) { _tenantId = tenantId; return this; }
    /// <summary>Sets task item ID on the builder so tests can override only scenario-specific values.</summary>
    public TaskItemTagBuilder WithTaskItemId(Guid taskItemId) { _taskItemId = taskItemId; return this; }
    /// <summary>Sets tag ID on the builder so tests can override only scenario-specific values.</summary>
    public TaskItemTagBuilder WithTagId(Guid tagId) { _tagId = tagId; return this; }

    /// <summary>Builds test data used by focused test cases.</summary>
    public TaskItemTag Build()
    {
        var result = TaskItemTag.Create(
            TenantId.From(_tenantId),
            TaskItemId.From(_taskItemId),
            TagId.From(_tagId));
        return result.Value!;
    }
}
