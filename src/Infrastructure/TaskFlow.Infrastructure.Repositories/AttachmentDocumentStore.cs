using System.Net.Http.Headers;
using EF.FlowEngine.Abstractions;
using EF.FlowEngine.Model;
using EF.Storage.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskFlow.Application.Contracts.Storage;
using TaskFlow.Domain.Shared;
using TaskFlow.Infrastructure.Data;

namespace TaskFlow.Infrastructure.Repositories;

/// <summary>
/// FlowEngine <see cref="IDocumentStore"/> over TaskFlow attachments (D-075). The store reference is an attachment
/// id; a read returns the content stored under that attachment's <c>StorageKey</c> in the lane's object storage
/// (Azure Blob or S3), never a name derived from its renamable file name.
/// <para>
/// The store enforces the tenant: the engine passes the executing instance's tenant, and a read without one, or of an
/// attachment that belongs to another tenant, is refused. The row is loaded with <c>IgnoreQueryFilters</c> and its
/// tenant compared with the instance's: the tenant query filter follows the ambient request context of the store's DI
/// scope (the system identity, which reads every tenant, when the engine runs in the background; the HTTP caller when a
/// resume runs inside a request), not the instance, and the explicit comparison also tells a foreign attachment apart
/// from a missing one.
/// </para>
/// <para>
/// Only UTF-8 text evidence (<c>text/plain</c>, <c>text/markdown</c>) is served, because the document node decodes every
/// read as UTF-8 text; any other content type is refused rather than handed to the agent as garbled text. Evidence larger than
/// <see cref="MaxEvidenceBytes"/> is refused too: the whole text goes into the agent prompt.
/// </para>
/// <c>UseDocumentStore</c> registers it as a singleton, so each read opens its own DI scope.
/// </summary>
public sealed class AttachmentDocumentStore(IServiceScopeFactory scopeFactory) : IDocumentStore
{
    /// <summary>Largest evidence read, in bytes (64 KiB); a larger attachment is refused before it is downloaded.</summary>
    public const int MaxEvidenceBytes = 64 * 1024;

    private static readonly string[] TextMediaTypes = ["text/plain", "text/markdown"];
    private static readonly string[] Utf8Charsets = ["utf-8", "us-ascii"];

    /// <summary>
    /// Opens the stored bytes of the attachment whose id is <paramref name="storeRef"/>, for a workflow instance of
    /// tenant <paramref name="tenantId"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The reference is not an attachment id, or the tenant is not a tenant id.</exception>
    /// <exception cref="UnauthorizedAccessException">
    /// The instance has no tenant, or the attachment belongs to another tenant.
    /// </exception>
    /// <exception cref="FileNotFoundException">No attachment has that id, or it has no uploaded content.</exception>
    /// <exception cref="NotSupportedException">The attachment is not UTF-8 text.</exception>
    /// <exception cref="InvalidDataException">The attachment is larger than <see cref="MaxEvidenceBytes"/>.</exception>
    public async Task<Stream> OpenReadAsync(string storeRef, string? tenantId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(storeRef, out var id))
            throw new ArgumentException($"Document reference '{storeRef}' is not an attachment id.", nameof(storeRef));
        if (tenantId is null)
            throw new UnauthorizedAccessException(
                $"Attachment {id} was requested by a workflow instance with no tenant; TaskFlow serves evidence only to an instance started for a tenant.");
        if (!Guid.TryParse(tenantId, out var tenant))
            throw new ArgumentException($"Workflow instance tenant '{tenantId}' is not a tenant id.", nameof(tenantId));

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TaskFlowDbContextQuery>();
        var attachmentId = AttachmentId.From(id);
        var attachment = await db.Attachments.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == attachmentId, ct)
            .ConfigureAwait(ConfigureAwaitOptions.None)
            ?? throw new FileNotFoundException($"Attachment {id} does not exist.");

        if (attachment.TenantId.Value != tenant)
            throw new UnauthorizedAccessException(
                $"Attachment {id} belongs to another tenant than the workflow instance's tenant {tenant}; a workflow reads only its own tenant's evidence.");

        if (!IsUtf8Text(attachment.ContentType))
            throw new NotSupportedException(
                $"Attachment {id} has content type '{attachment.ContentType}'; workflow evidence must be UTF-8 text/plain or text/markdown.");

        if (attachment.FileSizeBytes > MaxEvidenceBytes)
            throw TooLarge(id, attachment.FileSizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var blobs = scope.ServiceProvider.GetRequiredService<IObjectStorageRepository>();
        var blobName = attachment.StorageKey
            ?? throw new FileNotFoundException($"Attachment {id} is metadata only; it has no uploaded content.");
        // Buffered: the document node reads the whole text anyway, and the stream must not outlive this scope. The
        // copy is bounded as well, because the stored size is row metadata and the blob is what reaches the prompt.
        var buffer = new MemoryStream();
        await using (var content = await blobs.DownloadAsync(AttachmentBlobs.ContainerName, blobName, ct).ConfigureAwait(false))
        {
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await content.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxEvidenceBytes) throw TooLarge(id, $"more than {MaxEvidenceBytes}");
                buffer.Write(chunk, 0, read);
            }
        }

        buffer.Position = 0;
        return buffer;
    }

    private static InvalidDataException TooLarge(Guid id, string size) =>
        new($"Attachment {id} is {size} bytes; workflow evidence is limited to {MaxEvidenceBytes} bytes.");

    /// <summary>Not supported: workflows only read evidence; attachments are written through the public API.</summary>
    public Task<DocumentContextValue> StoreAsync(Stream content, string fileName, string contentType, string? tenantId, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "The attachment document store is read-only; upload attachments through POST /api/v1/attachments/upload.");

    /// <summary>True for text/plain or text/markdown with no charset or a UTF-8-compatible one.</summary>
    private static bool IsUtf8Text(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var parsed)
        && TextMediaTypes.Contains(parsed.MediaType, StringComparer.OrdinalIgnoreCase)
        && (parsed.CharSet is null || Utf8Charsets.Contains(parsed.CharSet.Trim('"'), StringComparer.OrdinalIgnoreCase));
}
