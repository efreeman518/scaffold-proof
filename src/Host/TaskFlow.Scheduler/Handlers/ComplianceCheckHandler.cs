using EF.BackgroundServices.Scheduling;
using EF.FlowEngine.Abstractions;
using EF.FlowEngine.Model;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.Json;
using TaskFlow.Application.Contracts.Repositories;

namespace TaskFlow.Scheduler.Handlers;

/// <summary>
/// The start path of the <c>compliance-check</c> workflow (D-075). Cross-tenant by construction: it streams, over the
/// system repository, the tenants with an open task tagged <c>compliance</c> due within
/// <c>Scheduling:Compliance:WindowDays</c>, and starts one instance per tenant through
/// <see cref="IFlowEngine.StartBackgroundAsync"/> with <see cref="StartRequest.TenantId"/> set, so the instance and its
/// <c>compliance-check-item</c> children read evidence as that tenant. The idempotency key is the tenant and the UTC
/// date of the run: FlowEngine returns the existing instance for a key it already holds, so a same-day re-run starts
/// nothing new and is not a failure.
/// <para>
/// One tenant's failed start does not hold back the others' daily check: every tenant is attempted, and the failures
/// are rethrown together at the end so the run fails visibly through <see cref="ScheduledJobRunner"/>. A re-run is
/// safe, since tenants already started that day resolve to their existing instance.
/// </para>
/// </summary>
public sealed class ComplianceCheckHandler(
    ITaskItemSystemRepository systemRepository,
    IFlowEngine engine,
    ScheduledJobTelemetry telemetry,
    TimeProvider timeProvider,
    IOptions<ComplianceCheckSettings> settings,
    ILogger<ComplianceCheckHandler> logger) : IScheduledJobHandler
{
    public const string JobName = "ComplianceCheck";
    public const string WorkflowId = "compliance-check";

    /// <summary>The tag the workflow's task search filters on (<c>compliance-check.json</c>, <c>n-query-due</c>).</summary>
    public const string TagName = "compliance";

    /// <summary>Tenants read per keyset page.</summary>
    private const int PageSize = 200;

    /// <summary>Starts one compliance-check instance per tenant with a due compliance task.</summary>
    public async Task HandleAsync(CancellationToken ct)
    {
        var asOfUtc = timeProvider.GetUtcNow();
        var dueBefore = asOfUtc.AddDays(settings.Value.WindowDays);
        var tenants = 0;
        var failures = new List<Exception>();

        await foreach (var tenantId in systemRepository.StreamTenantsWithTaggedOpenTasksDueAsync(TagName, dueBefore, PageSize, ct))
        {
            tenants++;
            try
            {
                var instance = await engine.StartBackgroundAsync(StartRequestFor(tenantId, asOfUtc, dueBefore), ct);
                logger.ComplianceCheckStarted(tenantId, instance.InstanceId, instance.Status);
            }
            // The job's own cancellation ends the run at once; anything else, a timeout included, is this tenant's failure.
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                failures.Add(new InvalidOperationException($"Starting {WorkflowId} for tenant {tenantId} failed.", ex));
            }
        }

        telemetry.RecordWork(JobName, tenants, tenants - failures.Count);
        logger.ComplianceCheckTenantsStarted(tenants - failures.Count, tenants);
        if (failures.Count > 0)
        {
            throw new AggregateException(
                $"{WorkflowId} failed to start for {failures.Count} of {tenants} tenants; the others were started.", failures);
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

    /// <summary><c>compliance-check:{tenant}:{yyyy-MM-dd}</c>, the UTC date of the run: one instance per tenant per day.</summary>
    public static string IdempotencyKey(Guid tenantId, DateTimeOffset asOfUtc) =>
        string.Create(CultureInfo.InvariantCulture, $"{WorkflowId}:{tenantId}:{asOfUtc.UtcDateTime:yyyy-MM-dd}");

    private static JsonContextValue Wrap(string value) => new() { Value = JsonSerializer.SerializeToElement(value) };
}
