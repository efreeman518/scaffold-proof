using EF.Common.Contracts;
using System.Text.Json.Serialization;
using TaskFlow.Application.Models.Paging;
using TaskFlow.Application.Models.Reads;
using TaskFlow.Application.Models.Shared;

namespace TaskFlow.Application.Models.Serialization;

/// <summary>
/// D-048: the one source-generated <see cref="JsonSerializerContext"/> for every TaskFlow HTTP/cache/UI
/// payload shape. Registered FIRST in each site's <c>TypeInfoResolverChain</c> with the reflection resolver
/// left behind it, so TaskFlow's own DTOs are serialized by generated code (no per-request reflection, no
/// runtime metadata build) while third-party shapes still resolve.
///
/// Wire format is unchanged by this registration. Verified empirically: the source generator does NOT bake
/// the declared naming policy into the generated property names - the policy comes from the
/// <see cref="System.Text.Json.JsonSerializerOptions"/> that owns the resolver chain at run time. So the same
/// context yields camelCase inside the Api's Web-defaults options and PascalCase inside the FusionCache
/// options, exactly as before it was inserted. The options below therefore matter only where
/// <c>TaskFlowJsonContext.Default.&lt;Type&gt;</c> is used directly (the NDJSON export writer), and they
/// mirror <c>RegisterApiServices.AddJsonOptions</c>: Web defaults plus the string enum converter.
///
/// Adding a public DTO/request/response/page type to this assembly without adding it here is a build-passing,
/// silently-reflective regression, which is what Test.Architecture <c>JsonContextCompletenessTests</c> exists
/// to catch.
/// </summary>
[JsonSourceGenerationOptions(
    // JsonSerializerDefaults.Web equivalent - ConfigureHttpJsonOptions starts from Web defaults.
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    // RegisterApiServices adds JsonStringEnumConverter; enums are named on the wire, not ordinals.
    UseStringEnumConverter = true)]
// Entity DTOs.
[JsonSerializable(typeof(AttachmentDto))]
[JsonSerializable(typeof(CategoryDto))]
[JsonSerializable(typeof(ChecklistItemDto))]
[JsonSerializable(typeof(CommentDto))]
[JsonSerializable(typeof(TagDto))]
[JsonSerializable(typeof(TaskItemDto))]
[JsonSerializable(typeof(TaskItemPatchDto))]
[JsonSerializable(typeof(TaskItemTagDto))]
[JsonSerializable(typeof(TenantInfoDto))]
[JsonSerializable(typeof(EntityBaseDto))]
// Read-model DTOs (summary, metadata, export row).
[JsonSerializable(typeof(TaskItemExportDto))]
[JsonSerializable(typeof(TaskItemStatusCountDto))]
[JsonSerializable(typeof(TaskItemSummaryDto))]
[JsonSerializable(typeof(TaskMetadataDto))]
// Search filters. Reached transitively through the request closures, declared so a filter used on its
// own (query-string binding, cache key payload) is generated too.
[JsonSerializable(typeof(DefaultSearchFilter))]
[JsonSerializable(typeof(AttachmentSearchFilter))]
[JsonSerializable(typeof(CategorySearchFilter))]
[JsonSerializable(typeof(ChecklistItemSearchFilter))]
[JsonSerializable(typeof(CommentSearchFilter))]
[JsonSerializable(typeof(TagSearchFilter))]
[JsonSerializable(typeof(TaskItemSearchFilter))]
// Request closures actually bound by the endpoints ([FromBody] DefaultRequest<T>, SearchRequest<TFilter>).
[JsonSerializable(typeof(DefaultRequest<AttachmentDto>))]
[JsonSerializable(typeof(DefaultRequest<CategoryDto>))]
[JsonSerializable(typeof(DefaultRequest<ChecklistItemDto>))]
[JsonSerializable(typeof(DefaultRequest<CommentDto>))]
[JsonSerializable(typeof(DefaultRequest<TagDto>))]
[JsonSerializable(typeof(DefaultRequest<TaskItemDto>))]
[JsonSerializable(typeof(DefaultRequest<TaskItemPatchDto>))]
[JsonSerializable(typeof(SearchRequest<AttachmentSearchFilter>))]
[JsonSerializable(typeof(SearchRequest<CategorySearchFilter>))]
[JsonSerializable(typeof(SearchRequest<ChecklistItemSearchFilter>))]
[JsonSerializable(typeof(SearchRequest<CommentSearchFilter>))]
[JsonSerializable(typeof(SearchRequest<TagSearchFilter>))]
[JsonSerializable(typeof(TaskItemCursorSearchRequest))]
// Response closures actually returned by the endpoints.
[JsonSerializable(typeof(DefaultResponse<AttachmentDto>))]
[JsonSerializable(typeof(DefaultResponse<CategoryDto>))]
[JsonSerializable(typeof(DefaultResponse<ChecklistItemDto>))]
[JsonSerializable(typeof(DefaultResponse<CommentDto>))]
[JsonSerializable(typeof(DefaultResponse<TagDto>))]
[JsonSerializable(typeof(DefaultResponse<TaskItemDto>))]
[JsonSerializable(typeof(DefaultResponse<TaskItemTagDto>))]
[JsonSerializable(typeof(PagedResponse<AttachmentDto>))]
[JsonSerializable(typeof(PagedResponse<CategoryDto>))]
[JsonSerializable(typeof(PagedResponse<ChecklistItemDto>))]
[JsonSerializable(typeof(PagedResponse<CommentDto>))]
[JsonSerializable(typeof(PagedResponse<TagDto>))]
[JsonSerializable(typeof(PagedResponse<TaskItemDto>))]
[JsonSerializable(typeof(CursorPage<TaskItemDto>))]
public partial class TaskFlowJsonContext : JsonSerializerContext
{
    /// <summary>
    /// The closed generic response and request shapes that no assembly scan can discover: a generic type
    /// definition has no <c>JsonTypeInfo</c>, so completeness for these can only be an explicit list. Kept
    /// here rather than in the test so the list lives next to the attributes it mirrors.
    /// </summary>
    public static readonly Type[] RegisteredClosedGenerics =
    [
        typeof(DefaultRequest<AttachmentDto>),
        typeof(DefaultRequest<CategoryDto>),
        typeof(DefaultRequest<ChecklistItemDto>),
        typeof(DefaultRequest<CommentDto>),
        typeof(DefaultRequest<TagDto>),
        typeof(DefaultRequest<TaskItemDto>),
        typeof(DefaultRequest<TaskItemPatchDto>),
        typeof(SearchRequest<AttachmentSearchFilter>),
        typeof(SearchRequest<CategorySearchFilter>),
        typeof(SearchRequest<ChecklistItemSearchFilter>),
        typeof(SearchRequest<CommentSearchFilter>),
        typeof(SearchRequest<TagSearchFilter>),
        typeof(DefaultResponse<AttachmentDto>),
        typeof(DefaultResponse<CategoryDto>),
        typeof(DefaultResponse<ChecklistItemDto>),
        typeof(DefaultResponse<CommentDto>),
        typeof(DefaultResponse<TagDto>),
        typeof(DefaultResponse<TaskItemDto>),
        typeof(DefaultResponse<TaskItemTagDto>),
        typeof(PagedResponse<AttachmentDto>),
        typeof(PagedResponse<CategoryDto>),
        typeof(PagedResponse<ChecklistItemDto>),
        typeof(PagedResponse<CommentDto>),
        typeof(PagedResponse<TagDto>),
        typeof(PagedResponse<TaskItemDto>),
        typeof(CursorPage<TaskItemDto>)
    ];
}
