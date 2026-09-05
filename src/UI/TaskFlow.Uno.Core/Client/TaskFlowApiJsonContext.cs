using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TaskFlow.Uno.Core.Business.Notifications;

namespace TaskFlow.Uno.Core.Client;

/// <summary>
/// Source-generated JSON contract for every concrete payload used by <see cref="TaskFlowApiClient"/>.
/// Uno WebAssembly release builds disable reflection-based JSON metadata, so transport code must
/// resolve all request, response, collection, and envelope types through this context.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TaskItemDto))]
[JsonSerializable(typeof(CategoryDto))]
[JsonSerializable(typeof(TagDto))]
[JsonSerializable(typeof(CommentDto))]
[JsonSerializable(typeof(ChecklistItemDto))]
[JsonSerializable(typeof(AttachmentDto))]
[JsonSerializable(typeof(DefaultRequest<TaskItemDto>))]
[JsonSerializable(typeof(DefaultRequest<CategoryDto>))]
[JsonSerializable(typeof(DefaultRequest<TagDto>))]
[JsonSerializable(typeof(DefaultRequest<CommentDto>))]
[JsonSerializable(typeof(DefaultRequest<ChecklistItemDto>))]
[JsonSerializable(typeof(DefaultRequest<AttachmentDto>))]
[JsonSerializable(typeof(DefaultResponse<TaskItemDto>))]
[JsonSerializable(typeof(DefaultResponse<CategoryDto>))]
[JsonSerializable(typeof(DefaultResponse<TagDto>))]
[JsonSerializable(typeof(DefaultResponse<CommentDto>))]
[JsonSerializable(typeof(DefaultResponse<ChecklistItemDto>))]
[JsonSerializable(typeof(DefaultResponse<AttachmentDto>))]
[JsonSerializable(typeof(TaskItemCursorSearchRequest))]
[JsonSerializable(typeof(CursorPage<TaskItemDto>))]
[JsonSerializable(typeof(TaskItemSummaryDto))]
[JsonSerializable(typeof(TaskMetadataDto))]
[JsonSerializable(typeof(SearchRequest<CategorySearchFilter>))]
[JsonSerializable(typeof(SearchRequest<TagSearchFilter>))]
[JsonSerializable(typeof(SearchRequest<AttachmentSearchFilter>))]
[JsonSerializable(typeof(PagedResponse<CategoryDto>))]
[JsonSerializable(typeof(PagedResponse<TagDto>))]
[JsonSerializable(typeof(PagedResponse<AttachmentDto>))]
[JsonSerializable(typeof(ProblemDetailsPayload))]
internal partial class TaskFlowApiJsonContext : JsonSerializerContext;

/// <summary>Routes every API client JSON operation through source-generated metadata.</summary>
internal static class TaskFlowApiJson
{
    internal static JsonTypeInfo<T> TypeInfo<T>() =>
        TaskFlowApiJsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
        ?? throw new InvalidOperationException($"Missing source-generated JSON metadata for {typeof(T)}.");

    internal static Task<HttpResponseMessage> PostAsync<T>(
        HttpClient http,
        string requestUri,
        T value,
        CancellationToken cancellationToken) =>
        http.PostAsJsonAsync(requestUri, value, TypeInfo<T>(), cancellationToken);

    /// <summary>Sends a PUT with the required If-Match precondition (D-021/GR-16): "*" overrides unconditionally.</summary>
    internal static Task<HttpResponseMessage> PutAsync<T>(
        HttpClient http,
        string requestUri,
        T value,
        string ifMatch,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, requestUri)
        {
            Content = JsonContent.Create(value, TypeInfo<T>())
        };
        request.Headers.IfMatch.Add(ToEntityTag(ifMatch));
        return http.SendAsync(request, cancellationToken);
    }

    /// <summary>Sends a DELETE with the required If-Match precondition (D-021/GR-16): "*" overrides unconditionally.</summary>
    internal static Task<HttpResponseMessage> DeleteAsync(
        HttpClient http,
        string requestUri,
        string ifMatch,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, requestUri);
        request.Headers.IfMatch.Add(ToEntityTag(ifMatch));
        return http.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// "*" is the explicit trusted-automation wildcard (D-032); EntityTagHeaderValue.Any is the .NET
    /// type for it since the constructor itself rejects "*" as an invalid quoted-string ETag. Anything
    /// else is a Version number, which needs the strong-ETag quotes IfMatchEndpointFilter parses.
    /// </summary>
    private static EntityTagHeaderValue ToEntityTag(string ifMatch) =>
        ifMatch == "*" ? EntityTagHeaderValue.Any : new EntityTagHeaderValue($"\"{ifMatch.Trim('"')}\"");

    internal static Task<T?> ReadAsync<T>(HttpContent content, CancellationToken cancellationToken) =>
        content.ReadFromJsonAsync(TypeInfo<T>(), cancellationToken);

    internal static Task<T?> GetAsync<T>(
        HttpClient http,
        string requestUri,
        CancellationToken cancellationToken) =>
        http.GetFromJsonAsync(requestUri, TypeInfo<T>(), cancellationToken);
}
