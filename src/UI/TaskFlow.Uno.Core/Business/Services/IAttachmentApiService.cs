using TaskFlow.Uno.Core.Business.Models;

namespace TaskFlow.Uno.Core.Business.Services;

/// <summary>Coordinates i attachment API application use cases with validation, tenant checks, repositories, and response shaping.</summary>
public interface IAttachmentApiService
{
    /// <summary>Searches search and returns filtered results for callers.</summary>
    Task<IReadOnlyList<AttachmentModel>> SearchAsync(Guid? ownerId = null, string? ownerType = null,
        CancellationToken ct = default);
    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    Task<AttachmentModel?> GetAsync(Guid id, CancellationToken ct = default);
    /// <summary>Creates requested data after validation and maps the result to the caller contract.</summary>
    Task<AttachmentModel> CreateAsync(AttachmentModel model, CancellationToken ct = default);
    /// <summary>Deletes requested data and maps failures to the caller contract. expectedVersion is sent as If-Match ("*" when null).</summary>
    Task DeleteAsync(Guid id, long? expectedVersion, CancellationToken ct = default);
}
