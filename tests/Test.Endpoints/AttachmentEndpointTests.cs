using EF.Storage.Contracts;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TaskFlow.Application.Contracts.Storage;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Shared.Enums;

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
        var response = await client.PutWithIfMatchAsync($"/api/v1/attachments/{created.Id}", new DefaultRequest<AttachmentDto> { Item = updateDto }, ConcurrencyHttpExtensions.IfMatch(created.Version!.Value), TestContext.CancellationToken);

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

        var response = await client.DeleteWithIfMatchAsync($"/api/v1/attachments/{created!.Id}", ConcurrencyHttpExtensions.IfMatch(created.Version!.Value), TestContext.CancellationToken);

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
        Assert.Contains("upload-test.txt", created.StorageUri);
        Assert.AreEqual(fileBytes.Length, created.FileSizeBytes);
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
