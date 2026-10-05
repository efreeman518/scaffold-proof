using EF.Domain;
using EF.Domain.Contracts;
using TaskFlow.Domain.Shared.Constants;
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

    /// <summary>
    /// Object-storage key of the content the server wrote at upload, set once and never changed (D-075). Reads and
    /// deletes use it rather than a name derived from <see cref="FileName"/>, which a caller can rename. Null for a
    /// metadata-only attachment (JSON create), which has no server-written content.
    /// </summary>
    public string? StorageKey { get; private set; }

    // Polymorphic owner
    public AttachmentOwnerType OwnerType { get; private set; }
    public Guid OwnerId { get; private set; }

    /// <summary>Initializes attachment with required dependencies and default state.</summary>
    private Attachment() { }

    /// <summary>Initializes attachment with required dependencies and default state.</summary>
    private Attachment(DomainTenantId tenantId, string fileName, string contentType, long fileSizeBytes, string storageUri, AttachmentOwnerType ownerType, Guid ownerId, DomainAttachmentId? id, string? storageKey)
    {
        if (id.HasValue) Id = id.Value; // D-033: caller-supplied UUIDv7 id makes create idempotent.
        TenantId = tenantId;
        FileName = fileName;
        ContentType = contentType;
        FileSizeBytes = fileSizeBytes;
        StorageUri = storageUri;
        OwnerType = ownerType;
        OwnerId = ownerId;
        StorageKey = storageKey;
    }

    /// <summary>Creates requested data after validation and maps the result to the caller contract.</summary>
    public static DomainResult<Attachment> Create(
        DomainTenantId tenantId, string fileName, string contentType,
        long fileSizeBytes, string storageUri,
        AttachmentOwnerType ownerType, Guid ownerId, DomainAttachmentId? id = null, string? storageKey = null)
    {
        var entity = new Attachment(tenantId, fileName, contentType, fileSizeBytes, storageUri, ownerType, ownerId, id, storageKey);
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

    /// <summary>
    /// The reason a file name is invalid, or null. A name is required and at most <see cref="DomainConstants.RULE_ATTACHMENT_FILENAME_LENGTH_MAX"/>
    /// characters. It is display metadata and the last segment of the object key, so it may not carry a path separator
    /// ('/' or '\'), a ".." segment, a control or format character (U+202E reverses displayed text) or a line or
    /// paragraph separator, nor end with '.' or whitespace (so a name made only of dots fails too), all of which a storage
    /// path, a file system or a download header could interpret.
    /// </summary>
    public static string? FileNameError(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return "File name is required.";
        if (fileName.Length > DomainConstants.RULE_ATTACHMENT_FILENAME_LENGTH_MAX)
            return $"File name cannot exceed {DomainConstants.RULE_ATTACHMENT_FILENAME_LENGTH_MAX} characters.";
        return fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains("..", StringComparison.Ordinal)
            || fileName[^1] == '.' || char.IsWhiteSpace(fileName[^1])
            || fileName.EnumerateRunes().Any(IsUnsafeRune)
            ? "File name cannot contain '/', '\\', '..', control or format characters or line or paragraph separators, or end with '.' or whitespace."
            : null;
    }

    // Cc, Cf, Zl and Zp, per code point, so a supplementary-plane format character (a tag character) is caught too.
    private static bool IsUnsafeRune(System.Text.Rune rune) =>
        System.Text.Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.Control
            or System.Globalization.UnicodeCategory.Format
            or System.Globalization.UnicodeCategory.LineSeparator
            or System.Globalization.UnicodeCategory.ParagraphSeparator;

    /// <summary>Creates a valid attachment instance with domain-required defaults.</summary>
    private DomainResult<Attachment> Valid()
    {
        var errors = new List<DomainError>();
        if (TenantId.Value == Guid.Empty) errors.Add(DomainError.Create("Tenant ID cannot be empty."));
        if (FileNameError(FileName) is { } fileNameError) errors.Add(DomainError.Create(fileNameError));
        if (StorageKey is not null && string.IsNullOrWhiteSpace(StorageKey)) errors.Add(DomainError.Create("Storage key cannot be blank."));
        if (string.IsNullOrWhiteSpace(ContentType)) errors.Add(DomainError.Create("Content type is required."));
        if (FileSizeBytes <= 0) errors.Add(DomainError.Create("File size must be greater than zero."));
        if (string.IsNullOrWhiteSpace(StorageUri)) errors.Add(DomainError.Create("Storage URI is required."));
        if (OwnerId == Guid.Empty) errors.Add(DomainError.Create("Owner ID cannot be empty."));
        return errors.Count > 0
            ? DomainResult<Attachment>.Failure(errors)
            : DomainResult<Attachment>.Success(this);
    }
}
