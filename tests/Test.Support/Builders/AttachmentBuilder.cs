using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Domain.Shared.Enums;

namespace Test.Support.Builders;

/// <summary>Builds attachment test data with sensible defaults so tests only override relevant fields.</summary>
public class AttachmentBuilder
{
    private Guid _tenantId = TestConstants.TenantId;
    private string _fileName = "test-file.pdf";
    private string _contentType = "application/pdf";
    private long _fileSizeBytes = 1024;
    private string _storageUri = "https://storage.example.com/test-file.pdf";
    private AttachmentOwnerType _ownerType = AttachmentOwnerType.TaskItem;
    private Guid _ownerId = Guid.NewGuid();

    /// <summary>Sets tenant ID on the builder so tests can override only scenario-specific values.</summary>
    public AttachmentBuilder WithTenantId(Guid tenantId) { _tenantId = tenantId; return this; }
    /// <summary>Sets file name on the builder so tests can override only scenario-specific values.</summary>
    public AttachmentBuilder WithFileName(string fileName) { _fileName = fileName; return this; }
    /// <summary>Sets content type on the builder so tests can override only scenario-specific values.</summary>
    public AttachmentBuilder WithContentType(string contentType) { _contentType = contentType; return this; }
    /// <summary>Sets file size bytes on the builder so tests can override only scenario-specific values.</summary>
    public AttachmentBuilder WithFileSizeBytes(long fileSizeBytes) { _fileSizeBytes = fileSizeBytes; return this; }
    /// <summary>Sets storage uri on the builder so tests can override only scenario-specific values.</summary>
    public AttachmentBuilder WithStorageUri(string storageUri) { _storageUri = storageUri; return this; }
    /// <summary>Sets owner type on the builder so tests can override only scenario-specific values.</summary>
    public AttachmentBuilder WithOwnerType(AttachmentOwnerType ownerType) { _ownerType = ownerType; return this; }
    /// <summary>Sets owner ID on the builder so tests can override only scenario-specific values.</summary>
    public AttachmentBuilder WithOwnerId(Guid ownerId) { _ownerId = ownerId; return this; }

    /// <summary>Builds test data used by focused test cases.</summary>
    public Attachment Build()
    {
        var result = Attachment.Create(
            TenantId.From(_tenantId),
            _fileName,
            _contentType,
            _fileSizeBytes,
            _storageUri,
            _ownerType,
            _ownerId);
        return result.Value!;
    }
}
