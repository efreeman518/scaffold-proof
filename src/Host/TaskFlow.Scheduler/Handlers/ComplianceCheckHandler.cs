using EF.BackgroundServices.Scheduling;
using EF.FlowEngine.Abstractions;
using EF.FlowEngine.Model;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.Json;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Contracts.Repositories;

namespace TaskFlow.Scheduler.Handlers;

/// <summary>
/// The start path of the <c>compliance-check</c> workflow (D-075). It streams, over the system repository, the tenants
/// with an open task tagged <c>compliance</c> due within <c>Scheduling:Compliance:WindowDays</c>, and starts one
/// instance through <see cref="IFlowEngine.StartBackgroundAsync"/> with <see cref="StartRequest.TenantId"/> set, so the
/// instance and its <c>compliance-check-item</c> children read evidence as that tenant.
/// <para>
/// The workflow's API calls (task search, attachment search, comment posts) authenticate as the scaffold principal, so
/// they read and write the scaffold tenant only (<see cref="SelfCallTenantId"/>). The job therefore starts the scaffold
/// tenant alone: an instance for any other tenant would search an empty page and report the tenant swept. Every other
/// qualifying tenant is logged as not started, a capability limit rather than a failure.
/// </para>
/// <para>
/// The idempotency key is the tenant and the UTC date of the run, so a same-day re-run resolves to the instance the
/// first run started and is not a failure. A failed start is collected and rethrown at the end of the run, so the run
/// fails visibly through <see cref="ScheduledJobRunner"/> after every tenant was handled.
/// </para>
/// </summary>
public sealed class ComplianceCheckHandler(
    ITaskItemSystemRepository systemRepository,
    IFlowEngine engine,
    IExecutionStateStore stateStore,
    ScheduledJobTelemetry telemetry,
    TimeProvider timeProvider,
    IOptions<ComplianceCheckSettings> settings,
    ILogger<ComplianceCheckHandler> logger) : IScheduledJobHandler
{
    public const string JobName = "ComplianceCheck";
    public const string WorkflowId = "compliance-check";

    /// <summary>The tag the workflow's task search filters on (<c>compliance-check.json</c>, <c>n-query-due</c>).</summary>
    public const string TagName = "compliance";

    /// <summary>
    /// The one tenant the workflow's API calls act for: the scaffold principal's tenant, the identity the Api
    /// authenticates every request as (<see cref="ScaffoldPrincipal"/>, shared with the Api's auth registration).
    /// Remove this restriction when the workflow's API client carries a service identity that acts for the instance
    /// tenant; the tenant stream already yields every qualifying tenant.
    /// </summary>
    public static readonly Guid SelfCallTenantId = Guid.Parse(ScaffoldPrincipal.TenantId);

    /// <summary>Tenants read per keyset page.</summary>
    private const int PageSize = 200;

    /// <summary>Starts the compliance-check instance for the tenant the workflow's API calls act for.</summary>
    public async Task HandleAsync(CancellationToken ct)
    {
        var asOfUtc = timeProvider.GetUtcNow();
        var dueBefore = asOfUtc.AddDays(settings.Value.WindowDays);
        var tenants = 0;
        var started = 0;
        var alreadyStarted = 0;
        var notStarted = 0;
        var failures = new List<Exception>();

        await foreach (var tenantId in systemRepository.StreamTenantsWithTaggedOpenTasksDueAsync(TagName, dueBefore, PageSize, ct))
        {
            tenants++;
            if (tenantId != SelfCallTenantId)
            {
                notStarted++;
                logger.ComplianceCheckTenantNotServed(tenantId, SelfCallTenantId);
                continue;
            }

            try
            {
                var request = StartRequestFor(tenantId, asOfUtc, dueBefore);
                if (await FindStartedAsync(request, ct) is { } existing)
                {
                    alreadyStarted++;
                    logger.ComplianceCheckAlreadyStarted(tenantId, existing.InstanceId, existing.Status);
                    continue;
                }

                var instance = await engine.StartBackgroundAsync(request, ct);
                started++;
                logger.ComplianceCheckStarted(tenantId, instance.InstanceId, instance.Status);
            }
            // The job's own cancellation ends the run at once; anything else, a timeout included, is this tenant's failure.
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                failures.Add(new InvalidOperationException($"Starting {WorkflowId} for tenant {tenantId} failed.", ex));
            }
        }

        telemetry.RecordWork(JobName, tenants, started);
        logger.ComplianceCheckRunSummary(tenants, started, alreadyStarted, notStarted, failures.Count);
        if (failures.Count > 0)
        {
            throw new AggregateException($"{WorkflowId} failed to start for {failures.Count} of {tenants} tenants.", failures);
        }
    }

    /// <summary>
    /// The instance already started under this request's key, found by its correlation id (which is the key).
    /// <para>
    /// Mitigation for EF.FlowEngine 1.0.207: <see cref="StartRequest.IdempotencyKey"/> is documented to return the existing
    /// instance, but the SQL state store's <c>QueryAsync</c> pages before it applies the tag filter the engine's key lookup
    /// uses (<c>Take = 1</c>), so the engine finds the original only while it is the newest instance in the store, and a
    /// re-run after another tenant's start creates a duplicate. <c>CorrelationId</c> is a column the store filters before
    /// paging. Remove this lookup when the state store applies the tag filter before paging and a same-day re-run in
    /// <c>AiWorkflowIntegrationTests.ComplianceCheckJob_StartsOneInstancePerQualifyingTenant_CarryingThatTenant</c> still
    /// yields one instance per tenant without it. Two runs racing for the same tenant can still both start; TickerQ runs
    /// one occurrence of the cron job at a time.
    /// </para>
    /// </summary>
    private async Task<ExecutionInstance?> FindStartedAsync(StartRequest request, CancellationToken ct)
    {
        var result = await stateStore.QueryAsync(
            new ExecutionQuery { WorkflowId = WorkflowId, CorrelationId = request.CorrelationId, Take = 1 }, ct);
        return result.Items.FirstOrDefault();
    }

    /// <summary>
    /// The start request for one tenant: the instance tenant and the <c>tenantId</c> param are the same tenant, and
    /// <c>dueBefore</c> is an ISO 8601 date-time as the workflow's params schema requires.
    /// </summary>
    public static StartRequest StartRequestFor(Guid tenantId, DateTimeOffset asOfUtc, DateTimeOffset dueBefore)
    {
        var tenant = tenantId.ToString();
        var key = IdempotencyKey(tenantId, asOfUtc);
        return new StartRequest
        {
            WorkflowId = WorkflowId,
            TenantId = tenant,
            CorrelationId = key,
            IdempotencyKey = key,
            Params = new Dictionary<string, ContextValue>
            {
                ["tenantId"] = Wrap(tenant),
                ["dueBefore"] = Wrap(dueBefore.ToString("O", CultureInfo.InvariantCulture)),
            },
        };
    }

    /// <summary>
    /// <c>compliance-check:{tenant}:{yyyy-MM-dd}</c>, the UTC date of the run: one instance per tenant per day. Also the
    /// correlation id, so it is exactly 64 characters, the width of the state store's <c>CorrelationId</c> column.
    /// </summary>
    public static string IdempotencyKey(Guid tenantId, DateTimeOffset asOfUtc) =>
        string.Create(CultureInfo.InvariantCulture, $"{WorkflowId}:{tenantId}:{asOfUtc.UtcDateTime:yyyy-MM-dd}");

    private static JsonContextValue Wrap(string value) => new() { Value = JsonSerializer.SerializeToElement(value) };
}
