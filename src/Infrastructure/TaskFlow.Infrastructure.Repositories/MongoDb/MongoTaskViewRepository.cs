using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using TaskFlow.Application.Contracts.Storage;

namespace TaskFlow.Infrastructure.Repositories.MongoDb;

/// <summary>Configuration for the explicit MongoDB TaskView read-model arm (D-038/D-060).</summary>
public sealed class MongoTaskViewSettings
{
    public const string DefaultDatabaseName = "taskflow";
    public const string DefaultCollectionName = "task-views";

    public MongoTaskViewSettings(string databaseName = DefaultDatabaseName, string collectionName = DefaultCollectionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        DatabaseName = databaseName;
        CollectionName = collectionName;
    }

    public string DatabaseName { get; }
    public string CollectionName { get; }
}

/// <summary>
/// MongoDB TaskView adapter with the same tenant, last-write-wins, counter, and keyset contracts as the
/// Cosmos and PostgreSQL JSONB implementations. A standalone server is sufficient: each operation is one
/// document command, so this arm deliberately adds neither transactions nor change streams.
/// </summary>
public sealed class MongoTaskViewRepository : ITaskViewRepository
{
    public const string IdentityIndexName = "UX_TaskView_TenantId_Id";
    public const string PagingIndexName = "IX_TaskView_TenantId_LastModifiedUtc_Id";

    private readonly IMongoDatabase database;
    private readonly IMongoCollection<MongoTaskViewDocument> collection;

    public MongoTaskViewRepository(string connectionString, MongoTaskViewSettings settings)
        : this(new MongoClient(connectionString), settings)
    {
    }

    internal MongoTaskViewRepository(IMongoClient client, MongoTaskViewSettings settings)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(settings);
        database = client.GetDatabase(settings.DatabaseName);
        collection = database.GetCollection<MongoTaskViewDocument>(settings.CollectionName);
    }

    /// <summary>Creates the two contract indexes. Called once under the shared startup provisioning lock.</summary>
    public Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        var keys = Builders<MongoTaskViewDocument>.IndexKeys;
        return collection.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<MongoTaskViewDocument>(
                    keys.Ascending(e => e.TenantId).Ascending(e => e.Id),
                    new CreateIndexOptions { Name = IdentityIndexName, Unique = true }),
                new CreateIndexModel<MongoTaskViewDocument>(
                    keys.Ascending(e => e.TenantId).Descending(e => e.LastModifiedUtc).Descending(e => e.Id),
                    new CreateIndexOptions { Name = PagingIndexName })
            ],
            cancellationToken: ct);
    }

    /// <summary>Runs a driver-level ping for the external-service health check.</summary>
    public Task<BsonDocument> CheckConnectivityAsync(CancellationToken ct = default) =>
        database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct);

    public Task UpsertAsync(TaskViewDto taskView, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(taskView);
        var filter = IdentityFilter(taskView.Id, taskView.TenantId);
        var update = Builders<MongoTaskViewDocument>.Update
            .SetOnInsert(e => e.StorageId, ObjectId.GenerateNewId())
            .Set(e => e.Id, taskView.Id)
            .Set(e => e.TenantId, taskView.TenantId)
            .Set(e => e.Title, taskView.Title)
            .Set(e => e.Description, taskView.Description)
            .Set(e => e.Status, taskView.Status)
            .Set(e => e.Priority, taskView.Priority)
            .Set(e => e.CategoryName, taskView.CategoryName)
            .Set(e => e.StartDate, ToUtcDateTime(taskView.StartDate))
            .Set(e => e.DueDate, ToUtcDateTime(taskView.DueDate))
            .Set(e => e.CompletedDate, ToUtcDateTime(taskView.CompletedDate))
            .Set(e => e.IsOverdue, taskView.IsOverdue)
            .Set(e => e.Tags, taskView.Tags)
            .Set(e => e.CommentCount, taskView.CommentCount)
            .Set(e => e.ChecklistTotal, taskView.ChecklistTotal)
            .Set(e => e.ChecklistCompleted, taskView.ChecklistCompleted)
            .Set(e => e.AttachmentCount, taskView.AttachmentCount)
            .Set(e => e.SubTaskCount, taskView.SubTaskCount)
            .Set(e => e.LastModifiedUtc, taskView.LastModifiedUtc.UtcDateTime)
            .Set(e => e.CreatedUtc, taskView.CreatedUtc.UtcDateTime);

        return collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, ct);
    }

    public async Task<TaskViewDto?> GetAsync(string id, string tenantId, CancellationToken ct = default)
    {
        var document = await collection.Find(IdentityFilter(id, tenantId)).FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return document is null ? null : MapToDto(document);
    }

    public async Task<TaskViewPage> QueryByTenantAsync(
        string tenantId, int pageSize = 20, string? continuationToken = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        var filters = Builders<MongoTaskViewDocument>.Filter;
        var filter = filters.Eq(e => e.TenantId, tenantId);
        if (!string.IsNullOrEmpty(continuationToken))
        {
            var after = TaskViewKeysetToken.Decode(continuationToken, tenantId);
            var timestamp = after.LastModifiedUtc.UtcDateTime;
            filter &= filters.Or(
                filters.Lt(e => e.LastModifiedUtc, timestamp),
                filters.And(
                    filters.Eq(e => e.LastModifiedUtc, timestamp),
                    filters.Lt(e => e.Id, after.Id)));
        }

        var rows = await collection.Find(filter)
            .SortByDescending(e => e.LastModifiedUtc).ThenByDescending(e => e.Id)
            .Limit(pageSize + 1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var hasMore = rows.Count > pageSize;
        if (hasMore) rows.RemoveAt(rows.Count - 1);

        var token = hasMore
            ? TaskViewKeysetToken.Encode(
                tenantId, new DateTimeOffset(rows[^1].LastModifiedUtc, TimeSpan.Zero), rows[^1].Id)
            : null;

        return new TaskViewPage([.. rows.Select(MapToDto)], token);
    }

    public Task PatchCountersAsync(string id, string tenantId,
        IReadOnlyDictionary<string, int> increments, DateTimeOffset lastModifiedUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(increments);
        var updates = new List<UpdateDefinition<MongoTaskViewDocument>>();
        AddIncrement(updates, increments, "commentCount", e => e.CommentCount);
        AddIncrement(updates, increments, "attachmentCount", e => e.AttachmentCount);
        AddIncrement(updates, increments, "checklistTotal", e => e.ChecklistTotal);
        AddIncrement(updates, increments, "checklistCompleted", e => e.ChecklistCompleted);
        if (updates.Count == 0) return Task.CompletedTask;

        updates.Add(Builders<MongoTaskViewDocument>.Update.Set(e => e.LastModifiedUtc, lastModifiedUtc.UtcDateTime));
        return collection.UpdateOneAsync(
            IdentityFilter(id, tenantId), Builders<MongoTaskViewDocument>.Update.Combine(updates), cancellationToken: ct);
    }

    public Task DeleteAsync(string id, string tenantId, CancellationToken ct = default) =>
        collection.DeleteOneAsync(IdentityFilter(id, tenantId), ct);

    private static FilterDefinition<MongoTaskViewDocument> IdentityFilter(string id, string tenantId) =>
        Builders<MongoTaskViewDocument>.Filter.And(
            Builders<MongoTaskViewDocument>.Filter.Eq(e => e.TenantId, tenantId),
            Builders<MongoTaskViewDocument>.Filter.Eq(e => e.Id, id));

    private static void AddIncrement(
        ICollection<UpdateDefinition<MongoTaskViewDocument>> updates,
        IReadOnlyDictionary<string, int> increments,
        string field,
        System.Linq.Expressions.Expression<Func<MongoTaskViewDocument, int>> property)
    {
        if (increments.TryGetValue(field, out var delta) && delta != 0)
            updates.Add(Builders<MongoTaskViewDocument>.Update.Inc(property, delta));
    }

    private static DateTime? ToUtcDateTime(DateTimeOffset? value) => value?.UtcDateTime;

    private static TaskViewDto MapToDto(MongoTaskViewDocument document) => new()
    {
        Id = document.Id,
        TenantId = document.TenantId,
        Title = document.Title,
        Description = document.Description,
        Status = document.Status,
        Priority = document.Priority,
        CategoryName = document.CategoryName,
        StartDate = ToDateTimeOffset(document.StartDate),
        DueDate = ToDateTimeOffset(document.DueDate),
        CompletedDate = ToDateTimeOffset(document.CompletedDate),
        IsOverdue = document.IsOverdue,
        Tags = document.Tags,
        CommentCount = document.CommentCount,
        ChecklistTotal = document.ChecklistTotal,
        ChecklistCompleted = document.ChecklistCompleted,
        AttachmentCount = document.AttachmentCount,
        SubTaskCount = document.SubTaskCount,
        LastModifiedUtc = new DateTimeOffset(document.LastModifiedUtc, TimeSpan.Zero),
        CreatedUtc = new DateTimeOffset(document.CreatedUtc, TimeSpan.Zero)
    };

    private static DateTimeOffset? ToDateTimeOffset(DateTime? value) =>
        value is null ? null : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));
}

/// <summary>MongoDB storage document. BSON dates provide chronological paging and preserve UTC semantics.</summary>
internal sealed class MongoTaskViewDocument
{
    [BsonId]
    public ObjectId StorageId { get; set; }

    [BsonElement("id")]
    public string Id { get; set; } = null!;

    [BsonElement("tenantId")]
    public string TenantId { get; set; } = null!;

    [BsonElement("title")]
    public string Title { get; set; } = null!;

    [BsonElement("description")]
    public string? Description { get; set; }

    [BsonElement("status")]
    public string Status { get; set; } = null!;

    [BsonElement("priority")]
    public string Priority { get; set; } = null!;

    [BsonElement("categoryName")]
    public string? CategoryName { get; set; }

    [BsonElement("startDate")]
    public DateTime? StartDate { get; set; }

    [BsonElement("dueDate")]
    public DateTime? DueDate { get; set; }

    [BsonElement("completedDate")]
    public DateTime? CompletedDate { get; set; }

    [BsonElement("isOverdue")]
    public bool IsOverdue { get; set; }

    [BsonElement("tags")]
    public List<string> Tags { get; set; } = [];

    [BsonElement("commentCount")]
    public int CommentCount { get; set; }

    [BsonElement("checklistTotal")]
    public int ChecklistTotal { get; set; }

    [BsonElement("checklistCompleted")]
    public int ChecklistCompleted { get; set; }

    [BsonElement("attachmentCount")]
    public int AttachmentCount { get; set; }

    [BsonElement("subTaskCount")]
    public int SubTaskCount { get; set; }

    [BsonElement("lastModifiedUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime LastModifiedUtc { get; set; }

    [BsonElement("createdUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedUtc { get; set; }
}
