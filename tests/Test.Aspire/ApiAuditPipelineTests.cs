using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Azure;
using Azure.Data.Tables;
using EF.Common.Contracts;
using EF.IntegrationTesting.Aspire;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using TaskFlow.Application.Models;
using TaskFlow.Infrastructure.Storage;

namespace Test.Aspire;

/// <summary>
/// End-to-end audit pipeline test for the API: POST /api/v1/categories -> API request handling ->
/// audit middleware -> Azurite Table Storage row, with a polling read-back to confirm the persisted entity.
/// Aspire tier (Aspire.Hosting.Testing) - required because two Aspire resources participate
/// (<c>taskflowapi</c> for the request, <c>TableStorage1</c> for verification), and both must be Healthy
/// before the test can run. The polling helper tolerates eventual consistency between request completion
/// and table visibility.
/// </summary>
[TestClass]
[TestCategory("Aspire")]
[DoNotParallelize]
public class ApiAuditPipelineTests
{
    private static readonly Guid ScaffoldTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    /// <summary>Boots the Aspire graph lazily on first mesh-test class to run; teardown is owned by <c>AspireMeshLifecycle</c>.</summary>
    [ClassInitialize]
    public static Task ClassInit(TestContext context) => AspireTestHost.EnsureStartedAsync(context);

    /// <summary>Verifies that given API category create, when request handled, then audit entry persisted to table storage.</summary>
    [TestMethod]
    [Timeout(1_200_000, CooperativeCancellation = true)]
    public async Task Given_ApiCategoryCreate_When_RequestHandled_Then_AuditEntryPersistedToTableStorage()
    {
        var ct = CancellationToken.None;

        // Resources can be Running before they're actually serving requests - wait for the health check.
        await AspireTestHost.WaitForResourceHealthyAsync("taskflowapi", ct);
        await AspireTestHost.WaitForResourceHealthyAsync("TableStorage1", ct);

        using var client = AspireTestHost.AspireApp!.CreateHttpClient("taskflowapi", "http");
        client.Timeout = TimeSpan.FromMinutes(10);
        var auditWindowStartUtc = DateTimeOffset.UtcNow;
        var request = new DefaultRequest<CategoryDto>
        {
            Item = new CategoryDto
            {
                Name = $"Api Audit {Guid.NewGuid():N}",
                Description = "Integration-created category",
                SortOrder = 1,
                IsActive = true
            }
        };

        using var response = await PostCreateCategoryWithRetryAsync(client, request, ct);
        var responseBody = await response.Content.ReadFromJsonAsync<DefaultResponse<CategoryDto>>(cancellationToken: ct);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        Assert.IsNotNull(responseBody);
        Assert.IsNotNull(responseBody.Item);
        Assert.IsNotNull(responseBody.Item.Id);
        Assert.AreEqual(ScaffoldTenantId, responseBody.Item.TenantId);
        Assert.AreEqual(request.Item.Name, responseBody.Item.Name);

        var connectionString = await AspireTestHost.AspireApp!.GetRequiredConnectionStringAsync(
            "TableStorage1",
            AspireTestHost.DefaultTimeout,
            ct);
        var tableClient = new TableServiceClient(connectionString).GetTableClient("taskflowaudit");
        var auditEntity = await WaitForAuditEntityAsync(
            tableClient,
            ScaffoldTenantId.ToString(),
            auditWindowStartUtc,
            response.StatusCode,
            ct);

        Assert.IsNotNull(auditEntity);
        // AuditLogRepository.PartitionKey (TaskFlow.Infrastructure.Storage) writes "{tenantId}|{yyyyMMdd}",
        // not the bare tenant id - assert the prefix, not exact equality.
        StringAssert.StartsWith(auditEntity.PartitionKey, $"{ScaffoldTenantId}|");
        Assert.AreEqual(ScaffoldTenantId.ToString(), auditEntity.TenantId);
        Assert.AreEqual("Category", auditEntity.EntityType);
        Assert.IsFalse(string.IsNullOrWhiteSpace(auditEntity.AuditId));
        Assert.AreEqual("Added", auditEntity.Action);
        Assert.AreEqual(AuditStatus.Success.ToString(), auditEntity.Status);
        Assert.IsGreaterThanOrEqualTo(auditWindowStartUtc, auditEntity.RecordedUtc);
    }

    /// <summary>
    /// Verifies that given an API category update sent with the ETag from the create response as
    /// If-Match, when the request is handled, then the update succeeds and a "Modified" audit entry is
    /// persisted to table storage - end to end coverage that If-Match on a real PUT reaches the audit
    /// pipeline the same way the POST create path already does.
    /// </summary>
    [TestMethod]
    [Timeout(1_200_000, CooperativeCancellation = true)]
    public async Task Given_ApiCategoryUpdateWithIfMatch_When_RequestHandled_Then_AuditEntryPersistedToTableStorage()
    {
        var ct = CancellationToken.None;

        await AspireTestHost.WaitForResourceHealthyAsync("taskflowapi", ct);
        await AspireTestHost.WaitForResourceHealthyAsync("TableStorage1", ct);

        using var client = AspireTestHost.AspireApp!.CreateHttpClient("taskflowapi", "http");
        client.Timeout = TimeSpan.FromMinutes(10);

        var createRequest = new DefaultRequest<CategoryDto>
        {
            Item = new CategoryDto
            {
                Name = $"Api Audit Update {Guid.NewGuid():N}",
                Description = "Integration-created category, updated in this test",
                SortOrder = 1,
                IsActive = true
            }
        };

        using var createResponse = await PostCreateCategoryWithRetryAsync(client, createRequest, ct);
        var createdBody = await createResponse.Content.ReadFromJsonAsync<DefaultResponse<CategoryDto>>(cancellationToken: ct);
        Assert.IsNotNull(createdBody?.Item?.Id);
        var etag = createResponse.Headers.ETag;
        Assert.IsNotNull(etag, "Create response is expected to carry an ETag (D-0xx: aggregate version).");

        var auditWindowStartUtc = DateTimeOffset.UtcNow;
        var updateRequest = new DefaultRequest<CategoryDto>
        {
            Item = new CategoryDto
            {
                Id = createdBody!.Item!.Id,
                Name = createdBody.Item.Name,
                Description = "Updated by Given_ApiCategoryUpdateWithIfMatch_When_RequestHandled_Then_AuditEntryPersistedToTableStorage",
                SortOrder = createdBody.Item.SortOrder,
                IsActive = createdBody.Item.IsActive
            }
        };
        using var updateHttpRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/categories/{createdBody.Item.Id}")
        {
            Content = JsonContent.Create(updateRequest)
        };
        updateHttpRequest.Headers.IfMatch.Add(etag!);

        using var updateResponse = await client.SendAsync(updateHttpRequest, ct);
        var updateResponseBody = await updateResponse.Content.ReadAsStringAsync(ct);
        Assert.AreEqual(HttpStatusCode.OK, updateResponse.StatusCode, $"Update did not succeed: {updateResponseBody}");

        var connectionString = await AspireTestHost.AspireApp!.GetRequiredConnectionStringAsync(
            "TableStorage1",
            AspireTestHost.DefaultTimeout,
            ct);
        var tableClient = new TableServiceClient(connectionString).GetTableClient("taskflowaudit");
        var auditEntity = await WaitForAuditEntityAsync(
            tableClient,
            ScaffoldTenantId.ToString(),
            auditWindowStartUtc,
            updateResponse.StatusCode,
            ct,
            expectedAction: "Modified");

        Assert.IsNotNull(auditEntity);
        Assert.AreEqual("Category", auditEntity.EntityType);
        Assert.AreEqual("Modified", auditEntity.Action);
        Assert.AreEqual(AuditStatus.Success.ToString(), auditEntity.Status);
    }

    /// <summary>Verifies post create category with retry behavior and protects the expected test contract.</summary>
    private static async Task<HttpResponseMessage> PostCreateCategoryWithRetryAsync(HttpClient client, object request, CancellationToken ct)
    {
        // 2 minutes: room for a slow-to-settle SQL Server first start (~40 s observed on the runner) plus
        // the API's own retries, without letting a genuinely broken run burn the full 4-minute window (and
        // therefore CI minutes) before failing. Not the ~900 s cumulative Aspire startup budget
        // (AspireTestHost.WaitForResourceHealthyAsync("taskflowdb"), which already passed before this method
        // runs) - that one is not the bottleneck either way.
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        HttpStatusCode? lastStatusCode = null;
        string? lastBody = null;
        Exception? lastException = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var response = await client.PostAsJsonAsync("/api/v1/categories", request, ct);
                if (response.StatusCode == HttpStatusCode.Created)
                    return response;

                lastStatusCode = response.StatusCode;
                lastBody = await response.Content.ReadAsStringAsync(ct);
                response.Dispose();
            }
            catch (HttpRequestException ex)
            {
                lastException = ex;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        if (lastException != null)
        {
            await AspireTestHost.DumpResourceDiagnosticsAsync("taskflowapi", ct);
            throw lastException;
        }

        Assert.Fail($"Category create API did not return 201. Last status: {lastStatusCode}; body: {lastBody}");
        throw new InvalidOperationException("Unreachable");
    }

    /// <summary>Verifies wait for audit entity behavior and protects the expected test contract.</summary>
    private static async Task<AuditLogTableEntity> WaitForAuditEntityAsync(
        TableClient tableClient,
        string tenantId,
        DateTimeOffset auditWindowStartUtc,
        HttpStatusCode lastRequestStatusCode,
        CancellationToken ct,
        string expectedAction = "Added")
    {
        // AuditLogRepository.PartitionKey (TaskFlow.Infrastructure.Storage) writes "{tenantId}|{yyyyMMdd}",
        // not the bare tenant id - retention deletes a whole expired day per tenant in one transaction.
        // Query the tenant's whole partition-key range (Azure Tables string-prefix technique: ge the
        // prefix, lt the prefix + the highest printable ASCII character) instead of an exact day key, so a
        // UTC day boundary crossed mid-poll cannot make an otherwise-correct row invisible. This is the bug
        // that produced the 2026-09 CI failures - not SQL Server startup timing.
        var partitionPrefix = $"{tenantId}|";
        var filter = TableClient.CreateQueryFilter($"PartitionKey ge {partitionPrefix} and PartitionKey lt {partitionPrefix + "~"}");

        // 2 minutes: room for a slow-to-settle SQL Server first start (~40 s observed on the runner) without
        // letting a genuinely broken run burn 4+ minutes of CI time before failing.
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        var tableFound = true;
        List<AuditLogTableEntity> recentEntities = [];

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                recentEntities.Clear();

                await foreach (var entity in tableClient.QueryAsync<AuditLogTableEntity>(filter, cancellationToken: ct))
                {
                    if (entity.RecordedUtc < auditWindowStartUtc)
                        continue;

                    recentEntities.Add(entity);

                    if (entity.EntityType == "Category" &&
                        entity.Action == expectedAction &&
                        entity.Status == AuditStatus.Success.ToString())
                    {
                        return entity;
                    }
                }
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                tableFound = false;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        var recentEntitySummary = recentEntities.Count == 0
            ? "none"
            : string.Join(
                "; ",
                recentEntities
                    .OrderByDescending(entity => entity.RecordedUtc)
                    .Take(5)
                    .Select(entity => $"{entity.PartitionKey}|{entity.RecordedUtc:O}|{entity.EntityType}|{entity.Action}|{entity.Status}|{entity.AuditId}|{entity.EntityKey}"));

        // Self-explaining failure: table existence and the triggering request's own status rule out an API
        // or storage-provisioning problem before anyone has to re-run with diagnostics.
        Assert.Fail(
            $"Expected recent audit entity for tenant '{tenantId}' (partition prefix '{partitionPrefix}') "
            + $"since '{auditWindowStartUtc:O}'. Table 'taskflowaudit' found: {tableFound}. Triggering "
            + $"request status: {(int)lastRequestStatusCode} {lastRequestStatusCode}. Recent entities: {recentEntitySummary}.");
        throw new InvalidOperationException("Unreachable");
    }
}
