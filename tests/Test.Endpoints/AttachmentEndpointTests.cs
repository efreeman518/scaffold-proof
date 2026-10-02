using EF.Common.Contracts;
using EF.Storage.Contracts;
using EF.Testing.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TaskFlow.Application.Contracts.Storage;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Shared.Enums;
using Test.Support;

namespace Test.Endpoints;

/// <summary>
/// HTTP contract tests for <c>/api/v1/attachments</c> CRUD plus the multipart upload endpoint, which is
/// covered using an in-memory <c>IObjectStorageRepository</c> swapped in via <c>WithWebHostBuilder</c>.
/// Endpoint tier (WebApplicationFactory + EF InMemory via <c>CustomApiFactory</c>): covers routing,
/// envelope shape, and multipart binding without an Azurite container.
/// </summary>
[TestClass]
public class AttachmentEndpointTests
{
    private static EndpointStyleFixture _fixture = null!;
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Initializes shared test fixtures before the class-level test run begins.</summary>
    [ClassInitialize]
    public static void ClassInit(TestContext _) => _fixture = new EndpointStyleFixture();

    /// <summary>Disposes shared test fixtures after the class-level test run finishes.</summary>
    [ClassCleanup]
    public static void ClassCleanup() => _fixture?.Dispose();

    /// <summary>Creates client used by the surrounding test cases.</summary>
    private static HttpClient CreateClient(string style) => _fixture.CreateClient(style);

    /// <summary>Creates parent task item used by the surrounding test cases.</summary>
    private async Task<Guid> CreateParentTaskItem(HttpClient client)
    {
        var dto = new TaskItemDto { Title = "ParentForAttachment", Priority = Priority.Medium };
        var response = await client.PostAsJsonAsync("/api/v1/task-items", new DefaultRequest<TaskItemDto> { Item = dto }, cancellationToken: TestContext.CancellationToken);
        var created = (await response.Content.ReadFromJsonAsync<DefaultResponse<TaskItemDto>>(_jsonOptions, TestContext.CancellationToken))!.Item;
        return created!.Id!.Value;
    }

    /// <summary>Verifies that given valid payload, when post attachment, then returns 201.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_ValidPayload_When_PostAttachment_Then_Returns201(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);
        var taskId = await CreateParentTaskItem(client);
        var dto = new AttachmentDto
        {
            FileName = "test.pdf",
            ContentType = "application/pdf",
            FileSizeBytes = 1024,
            StorageUri = "https://storage.example.com/test.pdf",
            OwnerType = AttachmentOwnerType.TaskItem,
            OwnerId = taskId
        };

        var response = await client.PostAsJsonAsync("/api/v1/attachments", new DefaultRequest<AttachmentDto> { Item = dto }, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<DefaultResponse<AttachmentDto>>(_jsonOptions, TestContext.CancellationToken))!.Item;
        Assert.IsNotNull(created);
        Assert.AreEqual("test.pdf", created.FileName);
    }

    /// <summary>Verifies that given existing attachment, when get by ID, then returns 200.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_ExistingAttachment_When_GetById_Then_Returns200(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);
        var taskId = await CreateParentTaskItem(client);
        var dto = new AttachmentDto
        {
            FileName = "get-test.docx",
            ContentType = "application/msword",
            FileSizeBytes = 2048,
            StorageUri = "https://storage.example.com/get-test.docx",
            OwnerType = AttachmentOwnerType.TaskItem,
            OwnerId = taskId
        };
        var createResponse = await client.PostAsJsonAsync("/api/v1/attachments", new DefaultRequest<AttachmentDto> { Item = dto }, cancellationToken: TestContext.CancellationToken);
        var created = (await createResponse.Content.ReadFromJsonAsync<DefaultResponse<AttachmentDto>>(_jsonOptions, TestContext.CancellationToken))!.Item;

        var response = await client.GetAsync($"/api/v1/attachments/{created!.Id}", TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<DefaultResponse<AttachmentDto>>(_jsonOptions, TestContext.CancellationToken))!.Item;
        Assert.IsNotNull(result);
        Assert.AreEqual("get-test.docx", result.FileName);
    }

    /// <summary>Verifies that given non existent ID, when get attachment, then returns 404.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_NonExistentId_When_GetAttachment_Then_Returns404(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);

        var response = await client.GetAsync($"/api/v1/attachments/{Guid.NewGuid()}", TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Verifies that given existing attachment, when put update, then returns 200.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_ExistingAttachment_When_PutUpdate_Then_Returns200(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);
        var taskId = await CreateParentTaskItem(client);
        var dto = new AttachmentDto
        {
            FileName = "before.png",
            ContentType = "image/png",
            FileSizeBytes = 512,
            StorageUri = "https://storage.example.com/before.png",
            OwnerType = AttachmentOwnerType.TaskItem,
            OwnerId = taskId
        };
        var createResponse = await client.PostAsJsonAsync("/api/v1/attachments", new DefaultRequest<AttachmentDto> { Item = dto }, cancellationToken: TestContext.CancellationToken);
        var created = (await createResponse.Content.ReadFromJsonAsync<DefaultResponse<AttachmentDto>>(_jsonOptions, TestContext.CancellationToken))!.Item;

        var updateDto = new AttachmentDto
        {
            Id = created!.Id,
            FileName = "after.png",
            ContentType = "image/png",
            FileSizeBytes = 1024,
            StorageUri = "https://storage.example.com/after.png",
            OwnerType = AttachmentOwnerType.TaskItem,
            OwnerId = taskId
        };
        var response = await client.PutAsJsonWithIfMatchAsync($"/api/v1/attachments/{created.Id}", new DefaultRequest<AttachmentDto> { Item = updateDto }, ConcurrencyHttpExtensions.FormatStrongETag(created.Version!.Value), JsonTestOptions.Default, TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<DefaultResponse<AttachmentDto>>(_jsonOptions, TestContext.CancellationToken))!.Item;
        Assert.AreEqual("after.png", updated!.FileName);
    }

    /// <summary>Verifies that given existing attachment, when delete, then returns 204.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_ExistingAttachment_When_Delete_Then_Returns204(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);
        var taskId = await CreateParentTaskItem(client);
        var dto = new AttachmentDto
        {
            FileName = "todelete.txt",
            ContentType = "text/plain",
            FileSizeBytes = 256,
            StorageUri = "https://storage.example.com/todelete.txt",
            OwnerType = AttachmentOwnerType.TaskItem,
            OwnerId = taskId
        };
        var createResponse = await client.PostAsJsonAsync("/api/v1/attachments", new DefaultRequest<AttachmentDto> { Item = dto }, cancellationToken: TestContext.CancellationToken);
        var created = (await createResponse.Content.ReadFromJsonAsync<DefaultResponse<AttachmentDto>>(_jsonOptions, TestContext.CancellationToken))!.Item;

        var response = await client.DeleteWithIfMatchAsync($"/api/v1/attachments/{created!.Id}", ConcurrencyHttpExtensions.FormatStrongETag(created.Version!.Value), TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);

        var getResponse = await client.GetAsync($"/api/v1/attachments/{created.Id}", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    /// <summary>Verifies that given file upload, when post upload, then returns 201 with blob uri.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_FileUpload_When_PostUpload_Then_Returns201WithBlobUri(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        // Create factory with in-memory blob storage
        using var uploadFactory = new CustomApiFactory(style).WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IObjectStorageRepository>(new InMemoryBlobStorageRepository());
            });
        });
        using var client = uploadFactory.CreateClient();
        var taskId = await CreateParentTaskItem(client);

        using var content = new MultipartFormDataContent();
        var fileBytes = "Hello, blob!"u8.ToArray();
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        content.Add(fileContent, "file", "upload-test.txt");
        content.Add(new StringContent(((int)AttachmentOwnerType.TaskItem).ToString()), "ownerType");
        content.Add(new StringContent(taskId.ToString()), "ownerId");

        var response = await client.PostAsync("/api/v1/attachments/upload", content, TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<DefaultResponse<AttachmentDto>>(_jsonOptions, TestContext.CancellationToken))!.Item;
        Assert.IsNotNull(created);
        Assert.AreEqual("upload-test.txt", created.FileName);
        // D-075: the object key is tenant, owner and a server-generated id; the file name is not part of it.
        StringAssert.StartsWith(created.StorageUri, $"https://inmemory.blob.local/{AttachmentBlobs.ContainerName}/{created.TenantId}/{taskId}/");
        Assert.DoesNotContain("upload-test.txt", created.StorageUri);
        Assert.AreEqual(fileBytes.Length, created.FileSizeBytes);
    }

    /// <summary>D-075: an upload whose file name carries a path or a ".." segment is a 400, and nothing is stored.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service, "../../00000000-0000-0000-0000-000000000002/task/x.txt")]
    [DataRow(EndpointStyles.Cqrs, "../../00000000-0000-0000-0000-000000000002/task/x.txt")]
    [DataRow(EndpointStyles.Service, "dir\\x.txt")]
    [TestMethod]
    public async Task Given_TraversalFileName_When_PostUpload_Then_Returns400(string style, string fileName)
    {
        EndpointStyles.SkipWhenStyleForced();
        var blobs = new InMemoryBlobStorageRepository();
        using var uploadFactory = UploadFactory(style, blobs);
        using var client = uploadFactory.CreateClient();
        var taskId = await CreateParentTaskItem(client);

        using var response = await UploadAsync(client, taskId, fileName);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.CancellationToken));
        Assert.IsEmpty((await blobs.ListAsync(AttachmentBlobs.ContainerName, cancellationToken: TestContext.CancellationToken)).Items);
    }

    /// <summary>D-075: renaming an attachment to a path or ".." name is a 400, in both endpoint styles.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_TraversalFileName_When_PutUpdate_Then_Returns400(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);
        var taskId = await CreateParentTaskItem(client);
        var dto = new AttachmentDto
        {
            FileName = "before.txt",
            ContentType = "text/plain",
            FileSizeBytes = 16,
            StorageUri = "https://storage.example.com/before.txt",
            OwnerType = AttachmentOwnerType.TaskItem,
            OwnerId = taskId
        };
        var createResponse = await client.PostAsJsonAsync("/api/v1/attachments", new DefaultRequest<AttachmentDto> { Item = dto }, cancellationToken: TestContext.CancellationToken);
        var created = (await createResponse.Content.ReadFromJsonAsync<DefaultResponse<AttachmentDto>>(_jsonOptions, TestContext.CancellationToken))!.Item!;

        var response = await client.PutAsJsonWithIfMatchAsync($"/api/v1/attachments/{created.Id}",
            new DefaultRequest<AttachmentDto> { Item = dto with { Id = created.Id, FileName = "../../other-tenant/x.txt" } },
            ConcurrencyHttpExtensions.FormatStrongETag(created.Version!.Value), JsonTestOptions.Default, TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.CancellationToken));
    }

    /// <summary>
    /// D-075: a rename changes only the display name. The delete removes the uploaded content by its stored key, so the
    /// renamed attachment's blob is the one deleted and no blob named after the new file name is touched.
    /// </summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_UploadedAttachment_When_RenamedThenDeleted_Then_TheUploadedBlobIsDeleted(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        var blobs = new InMemoryBlobStorageRepository();
        using var uploadFactory = UploadFactory(style, blobs);
        using var client = uploadFactory.CreateClient();
        var taskId = await CreateParentTaskItem(client);
        using var upload = await UploadAsync(client, taskId, "original.txt");
        Assert.AreEqual(HttpStatusCode.Created, upload.StatusCode);
        var created = (await upload.Content.ReadFromJsonAsync<DefaultResponse<AttachmentDto>>(_jsonOptions, TestContext.CancellationToken))!.Item!;
        var storedKeys = (await blobs.ListAsync(AttachmentBlobs.ContainerName, cancellationToken: TestContext.CancellationToken)).Items
            .Select(i => i.Name).ToArray();
        Assert.HasCount(1, storedKeys);

        var rename = await client.PutAsJsonWithIfMatchAsync($"/api/v1/attachments/{created.Id}",
            new DefaultRequest<AttachmentDto> { Item = created with { FileName = "renamed.txt" } },
            ConcurrencyHttpExtensions.FormatStrongETag(created.Version!.Value), JsonTestOptions.Default, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, rename.StatusCode, await rename.Content.ReadAsStringAsync(TestContext.CancellationToken));
        var renamed = (await rename.Content.ReadFromJsonAsync<DefaultResponse<AttachmentDto>>(_jsonOptions, TestContext.CancellationToken))!.Item!;
        Assert.IsTrue(await blobs.ExistsAsync(AttachmentBlobs.ContainerName, storedKeys[0], TestContext.CancellationToken),
            "a rename leaves the uploaded content where it was stored");

        var delete = await client.DeleteWithIfMatchAsync($"/api/v1/attachments/{created.Id}",
            ConcurrencyHttpExtensions.FormatStrongETag(renamed.Version!.Value), TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.IsEmpty((await blobs.ListAsync(AttachmentBlobs.ContainerName, cancellationToken: TestContext.CancellationToken)).Items,
            "the delete removes the uploaded content by its stored key");
    }

    // The derived factory owns the host; the base CustomApiFactory it came from never builds one of its own.
    /// <summary>An attachment content type filter that is empty, too long, a wildcard or not a media type is a 400.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service, "")]
    [DataRow(EndpointStyles.Cqrs, "")]
    [DataRow(EndpointStyles.Service, "text/*")]
    [DataRow(EndpointStyles.Cqrs, "not a media type")]
    [DataRow(EndpointStyles.Service, "eleven")]
    [DataRow(EndpointStyles.Service, "none")]
    [TestMethod]
    public async Task Given_InvalidContentTypeFilter_When_SearchAttachments_Then_Returns400(string style, string contentType)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);
        List<string> contentTypes = contentType switch
        {
            "eleven" => Enumerable.Repeat("text/plain", 11).ToList(),
            "none" => [],
            _ => [contentType]
        };

        var response = await client.PostAsJsonAsync("/api/v1/attachments/search",
            new SearchRequest<AttachmentSearchFilter> { PageIndex = 1, PageSize = 10, Filter = new AttachmentSearchFilter { ContentTypes = contentTypes } },
            cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.CancellationToken));
    }

    /// <summary>The content type filter returns the matching media types only, through both endpoint styles.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_TextAndBinaryAttachments_When_SearchByContentType_Then_ReturnsTextOnly(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);
        var taskId = await CreateParentTaskItem(client);
        foreach (var (name, type) in new[] { ("a.txt", "text/plain; charset=utf-8"), ("b.pdf", "application/pdf") })
        {
            var created = await client.PostAsJsonAsync("/api/v1/attachments", new DefaultRequest<AttachmentDto>
            {
                Item = new AttachmentDto { FileName = name, ContentType = type, FileSizeBytes = 8, StorageUri = "https://storage.example.com/" + name, OwnerType = AttachmentOwnerType.TaskItem, OwnerId = taskId }
            }, cancellationToken: TestContext.CancellationToken);
            Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        }

        var response = await client.PostAsJsonAsync("/api/v1/attachments/search",
            new SearchRequest<AttachmentSearchFilter> { PageIndex = 1, PageSize = 10, Filter = new AttachmentSearchFilter { OwnerId = taskId, ContentTypes = ["text/plain"] } },
            cancellationToken: TestContext.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        var page = JsonSerializer.Deserialize<PagedResponse<AttachmentDto>>(body, _jsonOptions)!;
        Assert.AreEqual("a.txt", page.Data.Single().FileName);
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> UploadFactory(string style, InMemoryBlobStorageRepository blobs) =>
        new CustomApiFactory(style).WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<IObjectStorageRepository>(blobs)));

    private Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid taskId, string fileName)
    {
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent("evidence"u8.ToArray());
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        content.Add(fileContent, "file", fileName);
        content.Add(new StringContent(((int)AttachmentOwnerType.TaskItem).ToString()), "ownerType");
        content.Add(new StringContent(taskId.ToString()), "ownerId");
        return client.PostAsync("/api/v1/attachments/upload", content, TestContext.CancellationToken);
    }

    public TestContext TestContext { get; set; } = null!;
}

/// <summary>Supports test execution for Test.endpoints scenarios.</summary>
internal class InMemoryBlobStorageRepository : IObjectStorageRepository
{
    private readonly Dictionary<string, byte[]> _blobs = new();

    /// <summary>Verifies upload behavior and protects the expected test contract.</summary>
    public async Task UploadAsync(string containerName, string objectName, Stream content,
        string? contentType = null, IDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, cancellationToken);
        _blobs[$"{containerName}/{objectName}"] = ms.ToArray();
    }

    /// <summary>Verifies download behavior and protects the expected test contract.</summary>
    public Task<Stream> DownloadAsync(string containerName, string objectName, CancellationToken cancellationToken = default)
    {
        var key = $"{containerName}/{objectName}";
        if (!_blobs.TryGetValue(key, out var data))
            throw new InvalidOperationException($"Blob {key} not found");
        return Task.FromResult<Stream>(new MemoryStream(data));
    }

    /// <summary>Verifies delete behavior and protects the expected test contract.</summary>
    public Task DeleteAsync(string containerName, string objectName, CancellationToken cancellationToken = default)
    {
        _blobs.Remove($"{containerName}/{objectName}");
        return Task.CompletedTask;
    }

    /// <summary>Verifies exists behavior and protects the expected test contract.</summary>
    public Task<bool> ExistsAsync(string containerName, string objectName, CancellationToken cancellationToken = default)
        => Task.FromResult(_blobs.ContainsKey($"{containerName}/{objectName}"));

    /// <summary>Verifies presigned URL behavior and protects the expected test contract.</summary>
    public Task<Uri> GetPresignedUrlAsync(string containerName, string objectName, TimeSpan lifetime,
        ObjectStoragePermissions permissions = ObjectStoragePermissions.Read,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new Uri($"https://inmemory.blob.local/{containerName}/{objectName}"));

    /// <summary>Verifies listing behavior and protects the expected test contract.</summary>
    public Task<ObjectStoragePage> ListAsync(string containerName, string? prefix = null,
        string? continuationToken = null, int pageSize = 100, CancellationToken cancellationToken = default)
    {
        var start = $"{containerName}/{prefix}";
        var items = _blobs.Keys
            .Where(k => k.StartsWith(start, StringComparison.Ordinal))
            .Take(pageSize)
            .Select(k => new ObjectStorageItem(k[(containerName.Length + 1)..], _blobs[k].LongLength, null, null))
            .ToList();
        return Task.FromResult(new ObjectStoragePage(items, null));
    }
}
