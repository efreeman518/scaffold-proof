using Grpc.Core;
using Grpc.Net.Client;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TaskFlow.Application.Models;
using TaskFlow.Contracts.Grpc;

namespace Test.Endpoints;

/// <summary>
/// D-054 contract tests for the internal gRPC read service. The claim they defend is parity: the same
/// tenant, the same instant, two transports, one answer. Each test seeds through REST and then asserts
/// the gRPC response against the REST response from the same host, so a mapper that drops a field or a
/// service that reads a different tenant fails here rather than in the dashboard.
///
/// The channel runs over the TestServer's in-memory handler, so there is no socket, no port, and no
/// dependency on the cleartext HTTP/2 endpoint being bindable on the build agent - the endpoint's own
/// declaration is covered by KestrelEndpointTests in Test.Architecture.
/// </summary>
[TestClass]
public class TaskFlowReadGrpcTests
{
    private static EndpointStyleFixture _fixture = null!;

    /// <summary>Initializes shared test fixtures before the class-level test run begins.</summary>
    [ClassInitialize]
    public static void ClassInit(TestContext _) => _fixture = new EndpointStyleFixture();

    /// <summary>Disposes shared test fixtures after the class-level test run finishes.</summary>
    [ClassCleanup]
    public static void ClassCleanup() => _fixture?.Dispose();

    /// <summary>Verifies the gRPC summary reports the same counts as the REST summary.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_SeededTasks_When_SummaryReadOverGrpc_Then_ItMatchesTheRestSummary(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        var factory = _fixture.Factory(style);
        using var client = factory.CreateClient();
        await SeedTaskAsync(client);

        using var channel = CreateChannel(factory);
        var reads = new TaskFlowRead.TaskFlowReadClient(channel);

        var overGrpc = (await reads.GetTaskItemSummaryAsync(
            new GetTaskItemSummaryRequest(), cancellationToken: TestContext.CancellationToken)).ToDto();

        using var restResponse = await client.GetAsync("/api/v1/task-items/summary", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, restResponse.StatusCode);
        using var rest = JsonDocument.Parse(
            await restResponse.Content.ReadAsStringAsync(TestContext.CancellationToken));

        Assert.IsGreaterThanOrEqualTo(1, overGrpc.Total);
        Assert.AreEqual(rest.RootElement.GetProperty("total").GetInt32(), overGrpc.Total);
        Assert.AreEqual(rest.RootElement.GetProperty("overdue").GetInt32(), overGrpc.Overdue);
        Assert.AreEqual(rest.RootElement.GetProperty("byStatus").GetArrayLength(), overGrpc.ByStatus.Count);
    }

    /// <summary>Verifies the gRPC metadata carries the same category and tag rows as the REST metadata.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_SeededMetadata_When_MetadataReadOverGrpc_Then_ItMatchesTheRestMetadata(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        var factory = _fixture.Factory(style);
        using var client = factory.CreateClient();

        var categoryName = $"GrpcCat-{Guid.NewGuid():N}";
        using var category = await client.PostAsJsonAsync("/api/v1/categories",
            new DefaultRequest<CategoryDto> { Item = new CategoryDto { Name = categoryName, IsActive = true } },
            cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Created, category.StatusCode);

        var tagName = $"grpctag-{Guid.NewGuid():N}";
        using var tag = await client.PostAsJsonAsync("/api/v1/tags",
            new DefaultRequest<TagDto> { Item = new TagDto { Name = tagName } },
            cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Created, tag.StatusCode);

        using var channel = CreateChannel(factory);
        var reads = new TaskFlowRead.TaskFlowReadClient(channel);

        var overGrpc = (await reads.GetTaskMetadataAsync(
            new GetTaskMetadataRequest(), cancellationToken: TestContext.CancellationToken)).ToDto();

        using var restResponse = await client.GetAsync("/api/v1/task-metadata", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, restResponse.StatusCode);
        using var rest = JsonDocument.Parse(
            await restResponse.Content.ReadAsStringAsync(TestContext.CancellationToken));

        Assert.Contains(c => c.Name == categoryName, overGrpc.Categories);
        Assert.Contains(t => t.Name == tagName, overGrpc.Tags);
        Assert.AreEqual(rest.RootElement.GetProperty("categories").GetArrayLength(), overGrpc.Categories.Count);
        Assert.AreEqual(rest.RootElement.GetProperty("tags").GetArrayLength(), overGrpc.Tags.Count);

        // The category the REST route returns and the one the gRPC route returns are the same record,
        // field for field - the point of the mapper test in Test.Unit, proven here against real data.
        var restCategory = rest.RootElement.GetProperty("categories").EnumerateArray()
            .Single(e => e.GetProperty("name").GetString() == categoryName);
        var grpcCategory = overGrpc.Categories.Single(c => c.Name == categoryName);
        Assert.AreEqual(restCategory.GetProperty("id").GetGuid(), grpcCategory.Id);
        Assert.AreEqual(restCategory.GetProperty("version").GetInt64(), grpcCategory.Version);
        Assert.AreEqual(restCategory.GetProperty("isActive").GetBoolean(), grpcCategory.IsActive);
    }

    /// <summary>Verifies a task read over gRPC carries the same fields the REST route returns.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_SeededTask_When_ReadByIdOverGrpc_Then_ItMatchesTheRestTask(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        var factory = _fixture.Factory(style);
        using var client = factory.CreateClient();

        var title = $"Grpc-{Guid.NewGuid():N}";
        using var created = await client.PostAsJsonAsync("/api/v1/task-items",
            new DefaultRequest<TaskItemDto> { Item = new TaskItemDto { Title = title } },
            cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        var seeded = await created.ItemAsync<TaskItemDto>(TestContext.CancellationToken);

        using var channel = CreateChannel(factory);
        var reads = new TaskFlowRead.TaskFlowReadClient(channel);

        var overGrpc = (await reads.GetTaskItemAsync(
            new GetTaskItemRequest { Id = seeded!.Id!.Value.ToString() },
            cancellationToken: TestContext.CancellationToken)).ToDto();

        Assert.AreEqual(seeded.Id, overGrpc.Id);
        Assert.AreEqual(seeded.Version, overGrpc.Version);
        Assert.AreEqual(seeded.TenantId, overGrpc.TenantId);
        Assert.AreEqual(title, overGrpc.Title);
        Assert.AreEqual(seeded.Status, overGrpc.Status);
        Assert.AreEqual(seeded.Priority, overGrpc.Priority);
    }

    /// <summary>An id that resolves to nothing is NOT_FOUND, the gRPC equivalent of the REST 404.</summary>
    [TestCategory("Endpoint")]
    [TestMethod]
    public async Task Given_UnknownId_When_ReadByIdOverGrpc_Then_ItIsNotFound()
    {
        EndpointStyles.SkipWhenStyleForced();
        var factory = _fixture.Factory(EndpointStyles.Service);
        using var client = factory.CreateClient();

        using var channel = CreateChannel(factory);
        var reads = new TaskFlowRead.TaskFlowReadClient(channel);

        var failure = await Assert.ThrowsExactlyAsync<RpcException>(async () =>
            await reads.GetTaskItemAsync(
                new GetTaskItemRequest { Id = Guid.NewGuid().ToString() },
                cancellationToken: TestContext.CancellationToken));

        Assert.AreEqual(StatusCode.NotFound, failure.StatusCode);
    }

    /// <summary>A malformed id is INVALID_ARGUMENT, the gRPC equivalent of the REST 400.</summary>
    [TestCategory("Endpoint")]
    [TestMethod]
    public async Task Given_MalformedId_When_ReadByIdOverGrpc_Then_ItIsInvalidArgument()
    {
        EndpointStyles.SkipWhenStyleForced();
        var factory = _fixture.Factory(EndpointStyles.Service);
        using var client = factory.CreateClient();

        using var channel = CreateChannel(factory);
        var reads = new TaskFlowRead.TaskFlowReadClient(channel);

        var failure = await Assert.ThrowsExactlyAsync<RpcException>(async () =>
            await reads.GetTaskItemAsync(
                new GetTaskItemRequest { Id = "not-a-guid" },
                cancellationToken: TestContext.CancellationToken));

        Assert.AreEqual(StatusCode.InvalidArgument, failure.StatusCode);
    }

    /// <summary>
    /// A channel over the TestServer. CreateClient() must have run first: it is what starts the host and
    /// makes Server available.
    /// </summary>
    private static GrpcChannel CreateChannel(CustomApiFactory factory) =>
        GrpcChannel.ForAddress(
            factory.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = factory.Server.CreateHandler() });

    /// <summary>Seeds one task so the tenant summary is not empty.</summary>
    private async Task SeedTaskAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/task-items",
            new DefaultRequest<TaskItemDto> { Item = new TaskItemDto { Title = $"Grpc-{Guid.NewGuid():N}" } },
            cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>MSTest-injected context; supplies the per-test cancellation token.</summary>
    public TestContext TestContext { get; set; } = null!;
}
