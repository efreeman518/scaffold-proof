using System.Text.Json;
using TaskFlow.Uno.Core.Business.Notifications;
using TaskFlow.Uno.Core.Client;

namespace Test.UI.Uno;

/// <summary>Guards the complete reflection-free JSON contract used by the Uno HTTP pipeline.</summary>
[TestClass]
[TestCategory("UI")]
public sealed class TaskFlowApiJsonContextTests
{
    private static readonly Type[] TransportTypes =
    [
        typeof(TaskItemDto),
        typeof(CategoryDto),
        typeof(TagDto),
        typeof(CommentDto),
        typeof(ChecklistItemDto),
        typeof(AttachmentDto),
        typeof(DefaultRequest<TaskItemDto>),
        typeof(DefaultRequest<CategoryDto>),
        typeof(DefaultRequest<TagDto>),
        typeof(DefaultRequest<CommentDto>),
        typeof(DefaultRequest<ChecklistItemDto>),
        typeof(DefaultRequest<AttachmentDto>),
        typeof(DefaultResponse<TaskItemDto>),
        typeof(DefaultResponse<CategoryDto>),
        typeof(DefaultResponse<TagDto>),
        typeof(DefaultResponse<CommentDto>),
        typeof(DefaultResponse<ChecklistItemDto>),
        typeof(DefaultResponse<AttachmentDto>),
        typeof(SearchRequest<TaskItemSearchFilter>),
        typeof(SearchRequest<CategorySearchFilter>),
        typeof(SearchRequest<TagSearchFilter>),
        typeof(SearchRequest<CommentSearchFilter>),
        typeof(SearchRequest<ChecklistItemSearchFilter>),
        typeof(SearchRequest<AttachmentSearchFilter>),
        typeof(PagedResponse<TaskItemDto>),
        typeof(PagedResponse<CategoryDto>),
        typeof(PagedResponse<TagDto>),
        typeof(PagedResponse<CommentDto>),
        typeof(PagedResponse<ChecklistItemDto>),
        typeof(PagedResponse<AttachmentDto>),
        typeof(ProblemDetailsPayload)
    ];

    /// <summary>Serializes and deserializes every concrete transport type without a reflection resolver.</summary>
    [TestMethod]
    public void SourceGeneratedContext_CoversEveryConcreteTransportType()
    {
        var options = new JsonSerializerOptions
        {
            TypeInfoResolver = TaskFlowApiJsonContext.Default
        };
        options.MakeReadOnly();

        foreach (var type in TransportTypes)
        {
            var typeInfo = options.GetTypeInfo(type);
            var instance = Activator.CreateInstance(type);

            Assert.IsNotNull(typeInfo, $"Missing source-generated metadata for {type}.");
            Assert.IsNotNull(instance, $"Transport type {type} must have a parameterless constructor.");

            var json = JsonSerializer.Serialize(instance, typeInfo);
            Assert.IsNotNull(JsonSerializer.Deserialize(json, typeInfo),
                $"Source-generated round trip failed for {type}.");
        }
    }
}
