using EF.Common.Contracts;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Shared.Enums;

namespace TaskFlow.Application.Contracts.Services;

/// <summary>Coordinates i attachment application use cases with validation, tenant checks, repositories, and response shaping.</summary>
public interface IAttachmentService
{
    /// <summary>Searches search and returns filtered results for callers.</summary>
    Task<PagedResponse<AttachmentDto>> SearchAsync(SearchRequest<AttachmentSearchFilter> request, bool includeTotal = false, CancellationToken ct = default);
    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    Task<Result<DefaultResponse<AttachmentDto>>> GetAsync(Guid id, CancellationToken ct = default);
    /// <summary>Creates requested data after validation and maps the result to the caller contract.</summary>
    Task<Result<DefaultResponse<AttachmentDto>>> CreateAsync(DefaultRequest<AttachmentDto> request, CancellationToken ct = default);
    /// <summary>Uploads upload to the configured storage backend and returns metadata.</summary>
    Task<Result<DefaultResponse<AttachmentDto>>> UploadAsync(Stream fileStream, string fileName, string contentType, long fileSizeBytes, AttachmentOwnerType ownerType, Guid ownerId, Guid? id = null, CancellationToken ct = default);
    /// <summary>Updates existing data after validation and preserves domain invariants.</summary>
    Task<Result<DefaultResponse<AttachmentDto>>> UpdateAsync(DefaultRequest<AttachmentDto> request, long? expectedVersion, CancellationToken ct = default);
    /// <summary>Deletes requested data and maps failures to the caller contract.</summary>
    Task<Result> DeleteAsync(Guid id, long? expectedVersion, CancellationToken ct = default);
}
