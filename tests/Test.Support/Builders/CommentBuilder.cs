using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;

namespace Test.Support.Builders;

/// <summary>Builds comment test data with sensible defaults so tests only override relevant fields.</summary>
public class CommentBuilder
{
    private Guid _tenantId = TestConstants.TenantId;
    private Guid _taskItemId = Guid.NewGuid();
    private string _body = "Test comment body";

    /// <summary>Sets tenant ID on the builder so tests can override only scenario-specific values.</summary>
    public CommentBuilder WithTenantId(Guid tenantId) { _tenantId = tenantId; return this; }
    /// <summary>Sets task item ID on the builder so tests can override only scenario-specific values.</summary>
    public CommentBuilder WithTaskItemId(Guid taskItemId) { _taskItemId = taskItemId; return this; }
    /// <summary>Sets body on the builder so tests can override only scenario-specific values.</summary>
    public CommentBuilder WithBody(string body) { _body = body; return this; }

    /// <summary>Builds test data used by focused test cases.</summary>
    public Comment Build()
    {
        var result = Comment.Create(
            TenantId.From(_tenantId),
            TaskItemId.From(_taskItemId),
            _body);
        return result.Value!;
    }
}
