using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Domain.Shared.Enums;

namespace Test.Support.Builders;

/// <summary>Builds task item test data with sensible defaults so tests only override relevant fields.</summary>
public class TaskItemBuilder
{
    private Guid _tenantId = TestConstants.TenantId;
    private string _title = "Test Task";
    private string? _description = "Test description";
    private Priority _priority = Priority.Medium;
    private Guid? _categoryId;
    private Guid? _parentTaskItemId;

    /// <summary>Sets tenant ID on the builder so tests can override only scenario-specific values.</summary>
    public TaskItemBuilder WithTenantId(Guid tenantId) { _tenantId = tenantId; return this; }
    /// <summary>Sets title on the builder so tests can override only scenario-specific values.</summary>
    public TaskItemBuilder WithTitle(string title) { _title = title; return this; }
    /// <summary>Sets description on the builder so tests can override only scenario-specific values.</summary>
    public TaskItemBuilder WithDescription(string? description) { _description = description; return this; }
    /// <summary>Sets priority on the builder so tests can override only scenario-specific values.</summary>
    public TaskItemBuilder WithPriority(Priority priority) { _priority = priority; return this; }
    /// <summary>Sets category ID on the builder so tests can override only scenario-specific values.</summary>
    public TaskItemBuilder WithCategoryId(Guid? categoryId) { _categoryId = categoryId; return this; }
    /// <summary>Sets parent task item ID on the builder so tests can override only scenario-specific values.</summary>
    public TaskItemBuilder WithParentTaskItemId(Guid? parentTaskItemId) { _parentTaskItemId = parentTaskItemId; return this; }

    /// <summary>Builds test data used by focused test cases.</summary>
    public TaskItem Build()
    {
        var result = TaskItem.Create(
            TenantId.From(_tenantId),
            _title,
            _description,
            _priority,
            DomainId.FromNullable<CategoryId>(_categoryId),
            DomainId.FromNullable<TaskItemId>(_parentTaskItemId));
        return result.Value!;
    }
}
