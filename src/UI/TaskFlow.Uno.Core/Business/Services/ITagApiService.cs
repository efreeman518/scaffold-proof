using TaskFlow.Uno.Core.Business.Models;

namespace TaskFlow.Uno.Core.Business.Services;

/// <summary>Coordinates i tag API application use cases with validation, tenant checks, repositories, and response shaping.</summary>
public interface ITagApiService
{
    /// <summary>Searches search and returns filtered results for callers.</summary>
    Task<IReadOnlyList<TagModel>> SearchAsync(string? searchTerm = null, CancellationToken ct = default);
    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    Task<TagModel?> GetAsync(Guid id, CancellationToken ct = default);
    /// <summary>Creates requested data after validation and maps the result to the caller contract.</summary>
    Task<TagModel> CreateAsync(TagModel model, CancellationToken ct = default);
    /// <summary>Updates existing data after validation and preserves domain invariants. expectedVersion is sent as If-Match ("*" when null).</summary>
    Task<TagModel> UpdateAsync(TagModel model, long? expectedVersion, CancellationToken ct = default);
    /// <summary>Deletes requested data and maps failures to the caller contract. expectedVersion is sent as If-Match ("*" when null).</summary>
    Task DeleteAsync(Guid id, long? expectedVersion, CancellationToken ct = default);
}
