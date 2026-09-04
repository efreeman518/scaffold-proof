using EF.Common.Contracts;
using EF.Data;
using EF.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.Mappers;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Domain.Shared.Enums;
using TaskFlow.Infrastructure.Data;

namespace TaskFlow.Infrastructure.Repositories;

/// <summary>Persists and queries attachment data through infrastructure storage contracts.</summary>
public class AttachmentRepositoryQuery(TaskFlowDbContextQuery db)
    : TaskFlowRepositoryQuery<Attachment, AttachmentId>(db), IAttachmentRepositoryQuery
{
    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    public async Task<Attachment?> GetAttachmentAsync(AttachmentId id, CancellationToken ct = default)
    {
        return await GetEntityAsync(
            false,
            filter: (Attachment a) => a.Id == id,
            cancellationToken: ct
        ).ConfigureAwait(ConfigureAwaitOptions.None);
    }

    /// <summary>Searches search attachments and returns filtered results for callers.</summary>
    public async Task<PagedResponse<AttachmentDto>> SearchAttachmentsAsync(SearchRequest<AttachmentSearchFilter> request, bool includeTotal = false, CancellationToken ct = default)
    {
        var q = DB.Set<Attachment>().ComposeIQueryable(false);

        // ordering
        if (request.Sorts?.Any() ?? false)
        {
            q = ((IOrderedQueryable<Attachment>)q.OrderBy(request.Sorts)).ThenBy(e => e.Id);
        }
        else
        {
            q = q.OrderBy(e => e.FileName).ThenBy(e => e.Id);
        }

        // filtering
        var filter = request.Filter;
        if (filter is not null)
        {
            var searchTerm = filter.SearchTerm?.Trim();
            if (!string.IsNullOrWhiteSpace(searchTerm))
                q = q.Where(e => e.FileName.Contains(searchTerm));

            if (filter.OwnerType.HasValue)
            {
                var ownerType = filter.OwnerType.Value;
                q = q.Where(e => e.OwnerType == ownerType);
            }

            if (filter.OwnerId.HasValue)
            {
                var ownerId = filter.OwnerId.Value;
                q = q.Where(e => e.OwnerId == ownerId);
            }

            if (filter.TenantId.HasValue)
            {
                var tenantId = DomainId.From<TenantId>(filter.TenantId.Value);
                q = q.Where(e => e.TenantId == tenantId);
            }
        }

        (var data, var total) = await q.QueryPageProjectionAsync(AttachmentMapper.Projection,
            pageSize: request.PageSize, pageIndex: Math.Max(1, request.PageIndex),
            includeTotal: includeTotal, splitQueryOptions: SplitQueryThresholdOptions.Default,
            cancellationToken: ct).ConfigureAwait(ConfigureAwaitOptions.None);

        return new PagedResponse<AttachmentDto>
        {
            PageIndex = request.PageIndex,
            PageSize = request.PageSize,
            Data = data,
            Total = total
        };
    }

    /// <summary>Provides the count by owner operation for attachment repository query.</summary>
    public async Task<int> CountByOwnerAsync(AttachmentOwnerType ownerType, Guid ownerId, CancellationToken ct = default)
        => await DB.Attachments.AsNoTracking().CountAsync(a => a.OwnerType == ownerType && a.OwnerId == ownerId, ct);
}
