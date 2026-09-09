using Aspire.Hosting.Testing;
using Azure;
using Azure.Data.Tables;
using EF.Common.Contracts;
using EF.IntegrationTesting.Aspire;
using System.Net;
using System.Net.Http.Json;
using TaskFlow.Application.Models;
using TaskFlow.Infrastructure.Storage;

namespace Test.Aspire;

/// <summary>
/// End-to-end audit pipeline test for the Functions host: POST /api/v1/categories on the
/// <c>taskflowfunctions</c> resource -> Function request handling -> audit middleware -> Azurite Table
/// Storage row, with a polling read-back.
/// Aspire tier (Aspire.Hosting.Testing) - required because the Functions host has the longest cold-start
/// of any resource and the test depends on both <c>taskflowfunctions</c> and <c>TableStorage1</c>. Missing
/// Core Tools fails unless <c>TASKFLOW_RUN_FUNCTIONS_TESTS=false</c> explicitly opts out.
/// </summary>
[TestClass]
[TestCategory("Aspire")]
[DoNotParallelize]
public class FunctionAuditPipelineTests
{
    private static readonly Guid FunctionFallbackTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    /// <summary>Boots the Aspire graph lazily on first mesh-test class to run; teardown is owned by <c>AspireMeshLifecycle</c>.</summary>
    [ClassInitialize]
    public static Task ClassInit(TestContext context) => AspireTestHost.EnsureStartedAsync(context);

    /// <summary>Verifies that given function category create, when request handled, then audit entry persisted to table storage.</summary>
    [TestMethod]
    [Timeout(1_200_000, CooperativeCancellation = true)]
    public async Task Given_FunctionCategoryCreate_When_RequestHandled_Then_AuditEntryPersistedToTableStorage()
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable(AspireTestHost.RunFunctionsTestsEnvironmentVariable),
                "false",
                StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive($"{AspireTestHost.RunFunctionsTestsEnvironmentVariable}=false - Functions full-stack test opted out.");
            return;
        }

        if (!AspireTestHost.EnsureFuncToolAvailable())
        {
            Assert.Fail("Azure Functions Core Tools ('func') is required. Install it or set TASKFLOW_RUN_FUNCTIONS_TESTS=false to opt out explicitly.");
            return;
        }

        var ct = CancellationToken.None;

        // Functions host has the longest cold-start of any resource - wait for health before issuing requests.
        await AspireTestHost.WaitForResourceHealthyAsync("taskflowfunctions", ct);
        await AspireTestHost.WaitForResourceHealthyAsync("TableStorage1", ct);

        try
        {
            using var client = AspireTestHost.AspireApp!.CreateHttpClient("taskflowfunctions", "http");
            client.Timeout = TimeSpan.FromMinutes(10);
            await AspireTestHost.RunStartupStepAsync(
                "Functions HTTP readiness",
                token => WaitForFunctionReadyAsync(client, token),
                ct);

            var auditWindowStartUtc = DateTimeOffset.UtcNow;
            var request = new
            {
                Name = $"Function Audit {Guid.NewGuid():N}",
                Description = "Integration-created category"
            };

            using var response = await PostCreateCategoryWithRetryAsync(client, request, ct);
            var responseBody = await response.Content.ReadFromJsonAsync<CategoryDto>(cancellationToken: ct);

            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
            Assert.IsNotNull(responseBody);
            Assert.IsNotNull(responseBody.Id);
            Assert.AreEqual(FunctionFallbackTenantId, responseBody.TenantId);
            Assert.AreEqual(request.Name, responseBody.Name);

            var connectionString = await AspireTestHost.AspireApp!.GetRequiredConnectionStringAsync(
                "TableStorage1",
                AspireTestHost.DefaultTimeout,
                ct);
            var tableClient = new TableServiceClient(connectionString).GetTableClient("taskflowaudit");
            var auditEntity = await WaitForAuditEntityAsync(
                tableClient,
                FunctionFallbackTenantId.ToString(),
                auditWindowStartUtc,
                response.StatusCode,
                ct);

            Assert.IsNotNull(auditEntity);
            // AuditLogRepository.PartitionKey (TaskFlow.Infrastructure.Storage) writes "{tenantId}|{yyyyMMdd}",
            // not the bare tenant id - assert the prefix, not exact equality.
            StringAssert.StartsWith(auditEntity.PartitionKey, $"{FunctionFallbackTenantId}|");
            Assert.AreEqual(FunctionFallbackTenantId.ToString(), auditEntity.TenantId);
            Assert.AreEqual("Category", auditEntity.EntityType);
            Assert.IsFalse(string.IsNullOrWhiteSpace(auditEntity.AuditId));
            Assert.AreEqual("Added", auditEntity.Action);
            Assert.AreEqual(AuditStatus.Success.ToString(), auditEntity.Status);
            Assert.IsGreaterThanOrEqualTo(auditWindowStartUtc, auditEntity.RecordedUtc);
        }
        catch
        {
            foreach (var resourceName in new[] { "taskflowfunctions", "taskflowdb", "TableStorage1", "ServiceBus1" })
            {
                await AspireTestHost.DumpResourceDiagnosticsAsync(resourceName, ct);
            }

            throw;
        }
    }

    /// <summary>Verifies the Functions host health endpoint becomes reachable before issuing the integration request.</summary>
    private static async Task WaitForFunctionReadyAsync(HttpClient client, CancellationToken ct)
    {
        Exception? lastException = null;

        try
        {
            while (true)
            {
                try
                {
                    using var response = await client.GetAsync("/api/health", ct);
                    if (response.IsSuccessStatusCode)
                        return;

                    var responseBody = await response.Content.ReadAsStringAsync(ct);
                    lastException = new InvalidOperationException(
                        $"Function health endpoint returned {(int)response.StatusCode} ({response.ReasonPhrase}). "
                        + $"Body: {Truncate(responseBody)}");
                }
                catch (HttpRequestException ex)
                {
                    lastException = ex;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                $"Function host did not become ready before the shared startup deadline. Last error: {lastException?.Message ?? "unknown"}",
                lastException ?? ex,
                ct);
        }
    }

    private static string Truncate(string value) => value.Length <= 2_000 ? value : value[..2_000] + "...";

    /// <summary>Verifies post create category with retry behavior and protects the expected test contract.</summary>
    private static async Task<HttpResponseMessage> PostCreateCategoryWithRetryAsync(HttpClient client, object request, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(180);
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
            catch (TaskCanceledException ex)
            {
                lastException = ex;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        if (lastException != null)
            throw lastException;

        Assert.Fail($"Category create function did not return 201. Last status: {lastStatusCode}; body: {lastBody}");
        throw new InvalidOperationException("Unreachable");
    }

    /// <summary>Verifies wait for audit entity behavior and protects the expected test contract.</summary>
    private static async Task<AuditLogTableEntity> WaitForAuditEntityAsync(
        TableClient tableClient,
        string tenantId,
        DateTimeOffset auditWindowStartUtc,
        HttpStatusCode lastRequestStatusCode,
        CancellationToken ct)
    {
        // AuditLogRepository.PartitionKey (TaskFlow.Infrastructure.Storage) writes "{tenantId}|{yyyyMMdd}",
        // not the bare tenant id - retention deletes a whole expired day per tenant in one transaction.
        // Query the tenant's whole partition-key range (Azure Tables string-prefix technique: ge the
        // prefix, lt the prefix + the highest printable ASCII character) instead of an exact day key, so a
        // UTC day boundary crossed mid-poll cannot make an otherwise-correct row invisible.
        var partitionPrefix = $"{tenantId}|";
        var filter = TableClient.CreateQueryFilter($"PartitionKey ge {partitionPrefix} and PartitionKey lt {partitionPrefix + "~"}");

        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
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
                        entity.Action == "Added" &&
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
