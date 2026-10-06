namespace TaskFlow.Application.Contracts.Storage;

/// <summary>
/// Attachment upload settings (D-075), bound from <see cref="ConfigSectionName"/>. Before an upload writes its blob it
/// saves a blob-delete reservation for the new key, and the save that inserts the attachment row removes it; when that
/// save never happens, the reservation deletes the orphaned blob once <see cref="OrphanBlobGrace"/> has passed.
/// </summary>
public sealed class AttachmentUploadSettings
{
    public const string ConfigSectionName = "AttachmentUpload";

    /// <summary>
    /// How long a reservation waits before the blob-delete worker may claim it. It must exceed the longest upload request
    /// (blob write plus row save), or the worker could delete the content of an upload still in flight; the default
    /// 15 minutes is far above the API request timeout (D-064). Positive.
    /// </summary>
    public TimeSpan OrphanBlobGrace { get; set; } = TimeSpan.FromMinutes(15);
}
