using Azure;
using Azure.Data.Tables;
using EF.Common.Contracts;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Globalization;
using TaskFlow.Application.Contracts.Storage;

namespace TaskFlow.Infrastructure.Storage;

// Uses IAzureClientFactory directly because TableRepositoryBase uses typeof(T).Name as the table name,
// but audit log requires a configurable table name from settings.
/// <summary>
/// Azure Table Storage audit sink. The partition key is <c>{tenantId}|{yyyyMMdd}</c> and RowKey uses reverse
/// ticks, so newest entries sort first inside a tenant-day and retention can delete a whole expired day with
/// single-partition transactions - Table Storage batches cannot span partitions.
/// </summary>
public class AuditLogRepository(
    IAzureClientFactory<TableServiceClient> clientFactory,
    IOptions<AuditLogStorageSettings> settings,
    ILogger<AuditLogRepository> logger) : IAuditLogRepository
{
    /// <summary>Table Storage caps one transactional batch at 100 entities, all in the same partition.</summary>
    public const int MaxTransactionSize = 100;

    private readonly TableServiceClient _client = clientFactory.CreateClient(settings.Value.TableServiceClientName);
    private readonly AuditLogStorageSettings _settings = settings.Value;
    private readonly ILogger<AuditLogRepository> _logger = logger;

    /// <summary>
    /// Persists one audit entry. Null and empty tenant ids use a configured partition prefix so
    /// global audit entries remain queryable.
    /// </summary>
    public async Task AppendAsync<TTenantId>(AuditEntry<string, TTenantId> entry, CancellationToken ct = default)
    {
        // No CreateIfNotExists here: the table is provisioned once by the EnsureExternalResources startup task.
        var table = _client.GetTableClient(_settings.TableName);

        var recordedUtc = DateTimeOffset.UtcNow;
        var tenantId = GetTenantId(entry.TenantId);
        var partitionKey = PartitionKey(tenantId ?? _settings.NullTenantPartitionKey, recordedUtc);
        var rowKey = $"{DateTime.MaxValue.Ticks - recordedUtc.UtcDateTime.Ticks:D19}_{entry.Id:N}";

        var entity = new AuditLogTableEntity
        {
            PartitionKey = partitionKey,
            RowKey = rowKey,
            Id = entry.Id,
            AuditId = entry.AuditId,
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

        await table.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct).ConfigureAwait(false);

        _logger.AuditEntryPersisted(entry.Id, partitionKey, entry.EntityType, entry.Action);
    }

    /// <inheritdoc />
    public async Task<int> PurgeOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default)
    {
        var table = _client.GetTableClient(_settings.TableName);

        // Server-side filter on the stored timestamp rather than a partition-key range: the tenant prefix
        // varies, so no single key range covers "every tenant's expired days".
        var filter = TableClient.CreateQueryFilter($"RecordedUtc lt {cutoffUtc}");

        var deleted = 0;
        var batches = new Dictionary<string, List<TableTransactionAction>>(StringComparer.Ordinal);

        await foreach (var row in table
            .QueryAsync<TableEntity>(filter, maxPerPage: MaxTransactionSize, select: ["PartitionKey", "RowKey"], ct)
            .ConfigureAwait(false))
        {
            if (!batches.TryGetValue(row.PartitionKey, out var batch))
            {
                batch = new List<TableTransactionAction>(MaxTransactionSize);
                batches[row.PartitionKey] = batch;
            }

            batch.Add(new TableTransactionAction(TableTransactionActionType.Delete, row, ETag.All));
            if (batch.Count < MaxTransactionSize) continue;

            deleted += await SubmitAsync(table, batch, ct).ConfigureAwait(false);
        }

        foreach (var batch in batches.Values)
            deleted += await SubmitAsync(table, batch, ct).ConfigureAwait(false);

        _logger.AuditEntriesPurged(deleted, cutoffUtc);
        return deleted;
    }

    /// <summary>Partition key for one tenant-day: retention deletes whole expired days per tenant.</summary>
    public static string PartitionKey(string tenantId, DateTimeOffset recordedUtc) =>
        $"{tenantId}|{recordedUtc.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";

    /// <summary>Submits and clears one single-partition transaction, returning the rows it removed.</summary>
    private static async Task<int> SubmitAsync(
        TableClient table, List<TableTransactionAction> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return 0;
        var count = batch.Count;
        await table.SubmitTransactionAsync(batch, ct).ConfigureAwait(false);
        batch.Clear();
        return count;
    }

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
