using EF.Data;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Infrastructure.Data;

namespace TaskFlow.Infrastructure.Repositories;

/// <summary>Persists and queries attachment data through infrastructure storage contracts.</summary>
public class AttachmentRepositoryTrxn(TaskFlowDbContextTrxn db)
    : TaskFlowRepositoryTrxn<Attachment, AttachmentId>(db), IAttachmentRepositoryTrxn
{
    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    public async Task<Attachment?> GetAttachmentAsync(AttachmentId id, CancellationToken ct = default)
    {
        return await GetEntityAsync(
            true,
            filter: (Attachment a) => a.Id == id,
            cancellationToken: ct
        ).ConfigureAwait(ConfigureAwaitOptions.None);
    }
}
