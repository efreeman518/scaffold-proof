using Azure.Data.Tables;
using EF.Audit.Contracts;
using EF.Common.Contracts;
using EF.Table;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Globalization;
using TaskFlow.Application.Contracts.Concurrency;

namespace TaskFlow.Infrastructure.Storage;

/// <summary>
/// Azure Table Storage arm of <see cref="IAuditLogRepository"/>. The partition key is
/// <c>{tenantId}|{yyyyMMdd}</c> and the row key leads with reverse ticks, so entries sort newest first
/// inside a tenant-day and retention can delete a whole expired day with single-partition transactions -
/// Table Storage batches cannot span partitions.
/// <para>
/// Both key components and <c>RecordedUtc</c> come from the audit message's own UUIDv7 id, never from this
/// consumer's clock: an audit message can be redelivered, and a key built from the write time would land a
/// replay on a second row instead of overwriting the first. That also makes the row key reproducible from
/// the message alone.
/// </para>
/// <para>
/// It derives from <see cref="TableRepositoryBase"/> for the retention sweep
/// (<see cref="TableRepositoryBase.DeleteByPartitionRangeAsync{T}"/>) but keeps its own
/// <see cref="TableClient"/> for reads and writes: the base's item operations name the table after the
/// entity type, and the audit trail's table name is configuration.
/// </para>
/// </summary>
public class AuditLogRepository(
    IAzureClientFactory<TableServiceClient> clientFactory,
    IOptions<AuditLogStorageSettings> settings,
    ILogger<AuditLogRepository> logger)
    : TableRepositoryBase(logger, Options.Create((TableRepositorySettingsBase)settings.Value), clientFactory),
      IAuditLogRepository
{
    private readonly TableClient _table = clientFactory
        .CreateClient(settings.Value.TableServiceClientName)
        .GetTableClient(settings.Value.TableName);
    private readonly AuditLogStorageSettings _settings = settings.Value;
    private readonly ILogger<AuditLogRepository> _logger = logger;


    /// <summary>
    /// Persists one audit entry. Null and empty tenant ids use the configured system sentinel so global
    /// audit entries remain queryable inside the tenant-first partition key.
    /// </summary>
    public async Task AppendAsync<TAuditIdType, TTenantIdType>(
        AuditEntry<TAuditIdType, TTenantIdType> entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // No CreateIfNotExists here: the table is provisioned once by the EnsureExternalResources startup task.
        var recordedUtc = UuidV7.TimestampOf(entry.Id);
        var tenantId = GetTenantId(entry.TenantId);
        var partitionKey = PartitionKey(tenantId ?? _settings.Audit.SystemTenantId, recordedUtc);

        var entity = new AuditLogTableEntity
        {
            PartitionKey = partitionKey,
            RowKey = RowKey(recordedUtc, entry.Id),
            Id = entry.Id,
            AuditId = ((object?)entry.AuditId)?.ToString() ?? string.Empty,
            TenantId = tenantId,
            EntityType = entry.EntityType,
            EntityKey = entry.EntityKey,
            Action = entry.Action,
            Status = entry.Status.ToString(),
            StartTimeTicks = entry.StartTime.Ticks,
            ElapsedTimeTicks = entry.ElapsedTime.Ticks,
            RecordedUtc = recordedUtc,
            Metadata = entry.Metadata,
            Error = entry.Error
        };

        await _table.UpsertEntityAsync(entity, Azure.Data.Tables.TableUpdateMode.Replace, cancellationToken).ConfigureAwait(false);

        _logger.AuditEntryPersisted(entry.Id, partitionKey, entry.EntityType, entry.Action);
    }

    /// <inheritdoc />
    public async Task<AuditLogPage> QueryAsync(AuditLogQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var pages = _table
            .QueryAsync<AuditLogTableEntity>(BuildFilter(query), query.PageSize, cancellationToken: cancellationToken)
            .AsPages(query.ContinuationToken, query.PageSize);

        await foreach (var page in pages.ConfigureAwait(false))
        {
            return new AuditLogPage(
                [.. page.Values.Select(ToRecord)],
                string.IsNullOrEmpty(page.ContinuationToken) ? null : page.ContinuationToken);
        }

        return new AuditLogPage([], null);
    }

    /// <inheritdoc />
    public async Task<int> PurgeOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken = default)
    {
        // Server-side filter on the stored recording time rather than a partition-key range: the tenant
        // prefix varies, so no single key range covers "every tenant's expired days". The empty prefix is
        // what tells EF.Table to sweep every partition.
        var deleted = await DeleteByPartitionRangeAsync<AuditLogTableEntity>(
            partitionKeyPrefix: string.Empty,
            cutoff: cutoffUtc,
            batchSize: _settings.Audit.PurgeBatchSize,
            tableName: _settings.TableName,
            timestampPropertyName: nameof(AuditLogTableEntity.RecordedUtc),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        _logger.AuditEntriesPurged(deleted, cutoffUtc);
        return deleted;
    }

    /// <summary>Partition key for one tenant-day: retention deletes whole expired days per tenant.</summary>
    public static string PartitionKey(string tenantId, DateTimeOffset recordedUtc) =>
        $"{tenantId}|{recordedUtc.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";

    /// <summary>Row key that sorts newest first inside a partition, disambiguated by the entry id.</summary>
    public static string RowKey(DateTimeOffset recordedUtc, Guid id) =>
        $"{DateTime.MaxValue.Ticks - recordedUtc.UtcDateTime.Ticks:D19}_{id:N}";

    /// <summary>
    /// OData filter for one query. A tenant filter becomes a partition-key prefix range up to
    /// <see cref="char.MaxValue"/> (the cheap path, since the tenant leads the partition key);
    /// everything else is a property comparison. Literals go through
    /// <see cref="TableClient.CreateQueryFilter(FormattableString)"/> so quoting and date formatting are
    /// the SDK's problem, not this method's.
    /// </summary>
    private string? BuildFilter(AuditLogQuery query)
    {
        var conditions = new List<string>(4);

        if (query.TenantId is not null)
        {
            var prefix = $"{(query.TenantId.Length == 0 ? _settings.Audit.SystemTenantId : query.TenantId)}|";
            conditions.Add(TableClient.CreateQueryFilter(
                $"PartitionKey ge {prefix} and PartitionKey lt {prefix + char.MaxValue}"));
        }

        if (query.FromUtc is { } fromUtc)
            conditions.Add(TableClient.CreateQueryFilter($"RecordedUtc ge {fromUtc}"));

        if (query.ToUtc is { } toUtc)
            conditions.Add(TableClient.CreateQueryFilter($"RecordedUtc lt {toUtc}"));

        if (query.EntityType is not null)
            conditions.Add(TableClient.CreateQueryFilter($"EntityType eq {query.EntityType}"));

        if (query.EntityKey is not null)
            conditions.Add(TableClient.CreateQueryFilter($"EntityKey eq {query.EntityKey}"));

        return conditions.Count == 0 ? null : string.Join(" and ", conditions);
    }

    /// <summary>Projects a stored row onto the backend-neutral record.</summary>
    private static AuditRecord ToRecord(AuditLogTableEntity entity) => new()
    {
        Id = entity.Id,
        RecordedUtc = entity.RecordedUtc,
        AuditId = entity.AuditId,
        TenantId = entity.TenantId,
        EntityType = entity.EntityType,
        EntityKey = entity.EntityKey,
        Action = entity.Action,
        Status = Enum.Parse<AuditStatus>(entity.Status),
        StartTime = TimeSpan.FromTicks(entity.StartTimeTicks),
        ElapsedTime = TimeSpan.FromTicks(entity.ElapsedTimeTicks),
        Metadata = entity.Metadata,
        Error = entity.Error
    };

    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    private static string? GetTenantId<TTenantId>(TTenantId tenantId)
    {
        object? tenantValue = tenantId;
        return tenantValue switch
        {
            null => null,
            Guid value when value == Guid.Empty => null,
            _ => tenantValue.ToString()
        };
    }
}
