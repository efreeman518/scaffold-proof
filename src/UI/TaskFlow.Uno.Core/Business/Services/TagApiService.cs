using TaskFlow.Uno.Core.Business.Models;
using TaskFlow.Uno.Core.Business.Notifications;
using TaskFlow.Uno.Core.Client;

namespace TaskFlow.Uno.Core.Business.Services;

/// <summary>Coordinates tag API application use cases with validation, tenant checks, repositories, and response shaping.</summary>
public class TagApiService(
    TaskFlowApiClient client,
    INotificationService notifications) : ITagApiService
{
    /// <summary>Searches search and returns filtered results for callers.</summary>
    public async Task<IReadOnlyList<TagModel>> SearchAsync(string? searchTerm = null,
        CancellationToken ct = default)
    {
        var response = await client.Api.Tags.Search.PostAsync(new()
        {
            Filter = new() { SearchTerm = searchTerm },
            PageIndex = 1,
            PageSize = 100
        }, cancellationToken: ct);

        return response?.Data?.Select(MapToModel).ToList() ?? [];
    }

    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    public async Task<TagModel?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var dto = await client.Api.Tags[id].GetAsync(cancellationToken: ct);
        return dto is null ? null : MapToModel(dto);
    }

    /// <summary>Creates requested data after validation and maps the result to the caller contract.</summary>
    public async Task<TagModel> CreateAsync(TagModel model, CancellationToken ct = default)
    {
        var dto = MapToDto(model);
        var result = await client.Api.Tags.PostAsync(dto, cancellationToken: ct);
        var created = MapToModel(result!);
        await notifications.ShowSuccess($"Created tag \"{created.Name}\".", ct: ct);
        return created;
    }

    /// <summary>Updates existing data after validation and preserves domain invariants.</summary>
    public async Task<TagModel> UpdateAsync(TagModel model, long? expectedVersion, CancellationToken ct = default)
    {
        var dto = MapToDto(model);
        var result = await client.Api.Tags[model.Id!.Value].PutAsync(dto, IfMatch(expectedVersion), cancellationToken: ct);
        var updated = MapToModel(result!);
        await notifications.ShowSuccess($"Updated tag \"{updated.Name}\".", ct: ct);
        return updated;
    }

    /// <summary>Deletes requested data and maps failures to the caller contract.</summary>
    public async Task DeleteAsync(Guid id, long? expectedVersion, CancellationToken ct = default)
    {
        await client.Api.Tags[id].DeleteAsync(IfMatch(expectedVersion), cancellationToken: ct);
        await notifications.ShowSuccess("Tag deleted.", ct: ct);
    }

    /// <summary>Formats a Version as the If-Match header value; null means the caller trusts the current state ("*").</summary>
    private static string IfMatch(long? expectedVersion) => expectedVersion?.ToString() ?? "*";

    /// <summary>Maps to model into the target contract used by callers.</summary>
    private static TagModel MapToModel(TagDto dto) => new()
    {
        Id = dto.Id,
        Version = dto.Version,
        Name = dto.Name ?? string.Empty,
        Color = dto.Color
    };

    /// <summary>Maps to DTO into the target contract used by callers.</summary>
    private static TagDto MapToDto(TagModel model) => new()
    {
        Id = model.Id,
        Version = model.Version,
        Name = model.Name,
        Color = model.Color
    };
}
