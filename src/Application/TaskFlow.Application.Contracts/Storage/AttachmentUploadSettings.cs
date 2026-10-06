namespace TaskFlow.Application.Contracts.Storage;

/// <summary>
/// Attachment upload settings (D-075), bound from <see cref="ConfigSectionName"/>. Before an upload writes its blob it
/// saves a blob-delete reservation for the new key, and the save that inserts the attachment row removes it; when that
/// save never happens, the reservation deletes the orphaned blob once <see cref="OrphanBlobGrace"/> has passed.
/// </summary>
public sealed class AttachmentUploadSettings
{
    public const string ConfigSectionName = "AttachmentUpload";

    /// <summary>Longest accepted <see cref="OrphanBlobGrace"/>: an orphaned blob is kept at most this long.</summary>
    public static readonly TimeSpan MaxOrphanBlobGrace = TimeSpan.FromDays(1);

    /// <summary>
    /// How long a reservation waits before the blob-delete worker may claim it. Startup validation requires it to exceed
    /// the API request timeout (D-064), which bounds an upload's blob write plus row insert, and to be at most
    /// <see cref="MaxOrphanBlobGrace"/>. An upload that still outlives it fails instead of keeping a row whose blob the
    /// worker deletes: the insert removes the reservation only while no worker holds it. Default 15 minutes.
    /// </summary>
    public TimeSpan OrphanBlobGrace { get; set; } = TimeSpan.FromMinutes(15);
}
