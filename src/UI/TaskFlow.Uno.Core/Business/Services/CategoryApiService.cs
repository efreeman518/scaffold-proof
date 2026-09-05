using TaskFlow.Uno.Core.Business.Models;
using TaskFlow.Uno.Core.Business.Notifications;
using TaskFlow.Uno.Core.Client;

namespace TaskFlow.Uno.Core.Business.Services;

/// <summary>Coordinates category API application use cases with validation, tenant checks, repositories, and response shaping.</summary>
public class CategoryApiService(
    TaskFlowApiClient client,
    INotificationService notifications) : ICategoryApiService
{
    /// <summary>Searches search and returns filtered results for callers.</summary>
    public async Task<IReadOnlyList<CategoryModel>> SearchAsync(string? searchTerm = null,
        bool? isActive = null, Guid? parentCategoryId = null, CancellationToken ct = default)
    {
        var response = await client.Api.Categories.Search.PostAsync(new()
        {
            Filter = new() { SearchTerm = searchTerm, IsActive = isActive, ParentCategoryId = parentCategoryId },
            PageIndex = 1,
            PageSize = 100
        }, cancellationToken: ct);

        return response?.Data?.Select(MapToModel).ToList() ?? [];
    }

    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    public async Task<CategoryModel?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var dto = await client.Api.Categories[id].GetAsync(cancellationToken: ct);
        return dto is null ? null : MapToModel(dto);
    }

    /// <summary>Creates requested data after validation and maps the result to the caller contract.</summary>
    public async Task<CategoryModel> CreateAsync(CategoryModel model, CancellationToken ct = default)
    {
        var dto = MapToDto(model);
        var result = await client.Api.Categories.PostAsync(dto, cancellationToken: ct);
        var created = MapToModel(result!);
        await notifications.ShowSuccess($"Created category \"{created.Name}\".", ct: ct);
        return created;
    }

    /// <summary>Updates existing data after validation and preserves domain invariants.</summary>
    public async Task<CategoryModel> UpdateAsync(CategoryModel model, long? expectedVersion, CancellationToken ct = default)
    {
        var dto = MapToDto(model);
        var result = await client.Api.Categories[model.Id!.Value].PutAsync(dto, IfMatch(expectedVersion), cancellationToken: ct);
        var updated = MapToModel(result!);
        await notifications.ShowSuccess($"Updated category \"{updated.Name}\".", ct: ct);
        return updated;
    }

    /// <summary>Deletes requested data and maps failures to the caller contract.</summary>
    public async Task DeleteAsync(Guid id, long? expectedVersion, CancellationToken ct = default)
    {
        await client.Api.Categories[id].DeleteAsync(IfMatch(expectedVersion), cancellationToken: ct);
        await notifications.ShowSuccess("Category deleted.", ct: ct);
    }

    /// <summary>Formats a Version as the If-Match header value; null means the caller trusts the current state ("*").</summary>
    private static string IfMatch(long? expectedVersion) => expectedVersion?.ToString() ?? "*";

    /// <summary>Maps to model into the target contract used by callers.</summary>
    private static CategoryModel MapToModel(CategoryDto dto) => new()
    {
        Id = dto.Id,
        Version = dto.Version,
        Name = dto.Name ?? string.Empty,
        Description = dto.Description,
        SortOrder = dto.SortOrder ?? 0,
        IsActive = dto.IsActive ?? true,
        ParentCategoryId = dto.ParentCategoryId
    };

    /// <summary>Maps to DTO into the target contract used by callers.</summary>
    private static CategoryDto MapToDto(CategoryModel model) => new()
    {
        Id = model.Id,
        Version = model.Version,
        Name = model.Name,
        Description = model.Description,
        SortOrder = model.SortOrder,
        IsActive = model.IsActive,
        ParentCategoryId = model.ParentCategoryId
    };
}
