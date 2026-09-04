using EF.Domain;
using EF.Domain.Contracts;
using TaskFlow.Domain.Shared.Enums;
using DomainAttachmentId = TaskFlow.Domain.Shared.AttachmentId;
using DomainTenantId = TaskFlow.Domain.Shared.TenantId;

namespace TaskFlow.Domain.Model;

/// <summary>Models attachment domain behavior and invariants.</summary>
public class Attachment : TaskFlowEntityBase<DomainAttachmentId>, ITenantEntity<DomainTenantId>
{
    public DomainTenantId TenantId { get; init; }
    public string FileName { get; private set; } = null!;
    public string ContentType { get; private set; } = null!;
    public long FileSizeBytes { get; private set; }
    public string StorageUri { get; private set; } = null!;

    // Polymorphic owner
    public AttachmentOwnerType OwnerType { get; private set; }
    public Guid OwnerId { get; private set; }

    /// <summary>Initializes attachment with required dependencies and default state.</summary>
    private Attachment() { }

    /// <summary>Initializes attachment with required dependencies and default state.</summary>
    private Attachment(DomainTenantId tenantId, string fileName, string contentType, long fileSizeBytes, string storageUri, AttachmentOwnerType ownerType, Guid ownerId)
    {
        TenantId = tenantId;
        FileName = fileName;
        ContentType = contentType;
        FileSizeBytes = fileSizeBytes;
        StorageUri = storageUri;
        OwnerType = ownerType;
        OwnerId = ownerId;
    }

    /// <summary>Creates requested data after validation and maps the result to the caller contract.</summary>
    public static DomainResult<Attachment> Create(
        DomainTenantId tenantId, string fileName, string contentType,
        long fileSizeBytes, string storageUri,
        AttachmentOwnerType ownerType, Guid ownerId)
    {
        var entity = new Attachment(tenantId, fileName, contentType, fileSizeBytes, storageUri, ownerType, ownerId);
        return entity.Valid();
    }

    /// <summary>Updates existing data after validation and preserves domain invariants.</summary>
    public DomainResult<Attachment> Update(string? fileName = null, string? contentType = null, long? fileSizeBytes = null, string? storageUri = null)
    {
        if (fileName is not null) FileName = fileName;
        if (contentType is not null) ContentType = contentType;
        if (fileSizeBytes.HasValue) FileSizeBytes = fileSizeBytes.Value;
        if (storageUri is not null) StorageUri = storageUri;
        return Valid();
    }

    /// <summary>Creates a valid attachment instance with domain-required defaults.</summary>
    private DomainResult<Attachment> Valid()
    {
        var errors = new List<DomainError>();
        if (TenantId.Value == Guid.Empty) errors.Add(DomainError.Create("Tenant ID cannot be empty."));
        if (string.IsNullOrWhiteSpace(FileName)) errors.Add(DomainError.Create("File name is required."));
        if (string.IsNullOrWhiteSpace(ContentType)) errors.Add(DomainError.Create("Content type is required."));
        if (FileSizeBytes <= 0) errors.Add(DomainError.Create("File size must be greater than zero."));
        if (string.IsNullOrWhiteSpace(StorageUri)) errors.Add(DomainError.Create("Storage URI is required."));
        if (OwnerId == Guid.Empty) errors.Add(DomainError.Create("Owner ID cannot be empty."));
        return errors.Count > 0
            ? DomainResult<Attachment>.Failure(errors)
            : DomainResult<Attachment>.Success(this);
    }
}
