using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace TaskFlow.Functions;

/// <summary>Configures function HTTP trigger host behavior for TaskFlow runtime services.</summary>
public class FunctionHttpTrigger(ILogger<FunctionHttpTrigger> logger, TimeProvider timeProvider)
{
    /// <summary>Provides the health check operation for function HTTP trigger.</summary>
    [Function("HealthCheck")]
    public async Task<HttpResponseData> HealthCheck(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequestData req)
    {
        logger.HealthCheckRequested(timeProvider.GetUtcNow().UtcDateTime);
        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        await response.WriteStringAsync("Healthy");
        return response;
    }

    /// <summary>Provides the task API proxy operation for function HTTP trigger.</summary>
    [Function("TaskApiProxy")]
    public async Task<HttpResponseData> TaskApiProxy(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "v1/tasks")] HttpRequestData req,
        CancellationToken ct)
    {
        logger.TaskApiProxyInvoked(timeProvider.GetUtcNow().UtcDateTime);

        // Read-only proxy: forwards to the task service for lightweight queries
        // Future: wire to ITaskItemService.SearchAsync for direct read access
        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = "TaskApiProxy placeholder - wire to ITaskItemService" }, ct);
        return response;
    }
}
