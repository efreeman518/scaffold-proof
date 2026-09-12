using System.Text.Json.Serialization;

namespace TaskFlow.Infrastructure.Data.ReadModel;

/// <summary>
/// Relational TaskView read-model row (D-038), the portable-lane counterpart of the Cosmos TaskView
/// document. Deliberately NOT an <c>ITenantEntity</c>: the tenant is passed explicitly on every call the way
/// a Cosmos partition key is, so there is no global query filter to remember and background projection work
/// needs no request context. No <c>Version</c> column and no audit stamping either - a read model is a
/// derivation of the write side, so auditing or version-checking it would audit the same fact twice and
/// invent a conflict where last-write-wins is correct. The write side remains the audited source of truth.
/// </summary>
public sealed class TaskViewRecord
{
    public string TenantId { get; set; } = null!;
    public string Id { get; set; } = null!;
    public string Title { get; set; } = null!;
    public string Status { get; set; } = null!;
    public string Priority { get; set; } = null!;
    public string? CategoryName { get; set; }
    public DateTimeOffset? StartDate { get; set; }
    public DateTimeOffset? DueDate { get; set; }
    public DateTimeOffset? CompletedDate { get; set; }
    public bool IsOverdue { get; set; }
    public int CommentCount { get; set; }
    public int ChecklistTotal { get; set; }
    public int ChecklistCompleted { get; set; }
    public int AttachmentCount { get; set; }
    public int SubTaskCount { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset LastModifiedUtc { get; set; }

    /// <summary>
    /// Serialized <see cref="TaskViewBody"/>: the parts of the projection nothing filters, sorts, or patches.
    /// The CLR string keeps repository serialization provider-neutral (D-030). PostgreSQL maps it to native
    /// jsonb while SQL Server retains its existing compatible string column. Scalar columns serve every
    /// current filter and sort, so no JSON-path index is created.
    /// </summary>
    public string Document { get; set; } = null!;
}

/// <summary>Free-form part of the projection stored in <see cref="TaskViewRecord.Document"/>.</summary>
/// <param name="Description">Task description, unindexed.</param>
/// <param name="Tags">Tag names, unindexed.</param>
public sealed record TaskViewBody(string? Description, List<string> Tags);

/// <summary>D-048: source-generated serialization for the read-model JSON column.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TaskViewBody))]
public partial class TaskViewBodyJsonContext : JsonSerializerContext;
