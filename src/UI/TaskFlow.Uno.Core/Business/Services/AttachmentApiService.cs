using TaskFlow.Uno.Core.Business.Models;
using TaskFlow.Uno.Core.Business.Notifications;
using TaskFlow.Uno.Core.Client;

namespace TaskFlow.Uno.Core.Business.Services;

/// <summary>Coordinates attachment API application use cases with validation, tenant checks, repositories, and response shaping.</summary>
public class AttachmentApiService(
    TaskFlowApiClient client,
    INotificationService notifications) : IAttachmentApiService
{
    /// <summary>Searches search and returns filtered results for callers.</summary>
    public async Task<IReadOnlyList<AttachmentModel>> SearchAsync(Guid? ownerId = null,
        string? ownerType = null, CancellationToken ct = default)
    {
        var response = await client.Api.Attachments.Search.PostAsync(new()
        {
            Filter = new() { OwnerId = ownerId, OwnerType = ownerType },
            PageIndex = 1,
            PageSize = 100
        }, cancellationToken: ct);

        return response?.Data?.Select(MapToModel).ToList() ?? [];
    }

    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    public async Task<AttachmentModel?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var dto = await client.Api.Attachments[id].GetAsync(cancellationToken: ct);
        return dto is null ? null : MapToModel(dto);
    }

    /// <summary>Creates requested data after validation and maps the result to the caller contract.</summary>
    public async Task<AttachmentModel> CreateAsync(AttachmentModel model, CancellationToken ct = default)
    {
        var dto = MapToDto(model);
        var result = await client.Api.Attachments.PostAsync(dto, cancellationToken: ct);
        var created = MapToModel(result!);
        await notifications.ShowSuccess($"Uploaded {created.FileName}.", ct: ct);
        return created;
    }

    /// <summary>Deletes requested data and maps failures to the caller contract.</summary>
    public async Task DeleteAsync(Guid id, long? expectedVersion, CancellationToken ct = default)
    {
        await client.Api.Attachments[id].DeleteAsync(IfMatch(expectedVersion), cancellationToken: ct);
        await notifications.ShowSuccess("Attachment deleted.", ct: ct);
    }

    /// <summary>Formats a Version as the If-Match header value; null means the caller trusts the current state ("*").</summary>
    private static string IfMatch(long? expectedVersion) => expectedVersion?.ToString() ?? "*";

    /// <summary>Maps to model into the target contract used by callers.</summary>
    private static AttachmentModel MapToModel(AttachmentDto dto) => new()
    {
        Id = dto.Id,
        Version = dto.Version,
        FileName = dto.FileName ?? string.Empty,
        ContentType = dto.ContentType ?? string.Empty,
        FileSizeBytes = dto.FileSizeBytes ?? 0,
        StorageUri = dto.StorageUri ?? string.Empty,
        OwnerType = dto.OwnerType ?? "TaskItem",
        OwnerId = dto.OwnerId ?? Guid.Empty
    };

    /// <summary>Maps to DTO into the target contract used by callers.</summary>
    private static AttachmentDto MapToDto(AttachmentModel model) => new()
    {
        FileName = model.FileName,
        ContentType = model.ContentType,
        FileSizeBytes = model.FileSizeBytes,
        StorageUri = model.StorageUri,
        OwnerType = model.OwnerType,
        OwnerId = model.OwnerId
    };
}
