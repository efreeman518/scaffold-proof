using EF.BackgroundServices.Scheduling;
using EF.FlowEngine.Abstractions;
using EF.FlowEngine.Model;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.Json;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Bootstrapper;

namespace TaskFlow.Scheduler.Handlers;

/// <summary>
/// The start path of the <c>compliance-check</c> workflow (D-075). It streams, over the system repository, the tenants
/// with an open task tagged <c>compliance</c> due within <c>Scheduling:Compliance:WindowDays</c>, and starts one
/// instance through <see cref="IFlowEngine.StartBackgroundAsync"/> with <see cref="StartRequest.TenantId"/> set, so the
/// instance and its <c>compliance-check-item</c> children read evidence as that tenant.
/// <para>
/// The job starts a tenant only when the workflow's API calls (task search, attachment search, comment posts) can act
/// for it (<see cref="SelfCallRelayOptions.CanActFor"/>). With the self-call relay configured they act for the instance
/// tenant (D-068), so every qualifying tenant starts. Without it they authenticate as the scaffold principal and read and
/// write the scaffold tenant only, so the job starts that tenant alone: an instance for any other tenant would search an
/// empty page and report the tenant swept. Every tenant it cannot serve is logged as not started, a capability limit
/// rather than a failure.
/// </para>
/// <para>
/// The idempotency key is the tenant and the UTC date of the run, so the engine resolves a same-day re-run to the
/// instance the first run started (<see cref="StartRequest.IdempotencyKey"/>), and that is not a failure. A failed start is collected and rethrown at the end of the run, so the run
/// fails visibly through <see cref="ScheduledJobRunner"/> after every tenant was handled.
/// </para>
/// </summary>
public sealed class ComplianceCheckHandler(
    ITaskItemSystemRepository systemRepository,
    IFlowEngine engine,
    ScheduledJobTelemetry telemetry,
    TimeProvider timeProvider,
    IOptions<ComplianceCheckSettings> settings,
    IOptions<SelfCallRelayOptions> selfCall,
    ILogger<ComplianceCheckHandler> logger) : IScheduledJobHandler
{
    public const string JobName = "ComplianceCheck";
    public const string WorkflowId = "compliance-check";

    /// <summary>The tag the workflow's task search filters on (<c>compliance-check.json</c>, <c>n-query-due</c>).</summary>
    public const string TagName = "compliance";

    /// <summary>Tenants read per keyset page.</summary>
    private const int PageSize = 200;

    /// <summary>Starts the compliance-check instance for every qualifying tenant the workflow's API calls can act for.</summary>
    public async Task HandleAsync(CancellationToken ct)
    {
        var asOfUtc = timeProvider.GetUtcNow();
        var dueBefore = asOfUtc.AddDays(settings.Value.WindowDays);
        var tenants = 0;
        var started = 0;
        var notStarted = 0;
        var failures = new List<Exception>();

        await foreach (var tenantId in systemRepository.StreamTenantsWithTaggedOpenTasksDueAsync(TagName, dueBefore, PageSize, ct))
        {
            tenants++;
            if (!selfCall.Value.CanActFor(tenantId))
            {
                notStarted++;
                logger.ComplianceCheckTenantNotServed(tenantId, SelfCallRelayOptions.ScaffoldTenantId);
                continue;
            }

            try
            {
                var instance = await engine.StartBackgroundAsync(StartRequestFor(tenantId, asOfUtc, dueBefore), ct);
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
        logger.ComplianceCheckRunSummary(tenants, started, notStarted, failures.Count);
        if (failures.Count > 0)
        {
            throw new AggregateException($"{WorkflowId} failed to start for {failures.Count} of {tenants} tenants.", failures);
        }
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
