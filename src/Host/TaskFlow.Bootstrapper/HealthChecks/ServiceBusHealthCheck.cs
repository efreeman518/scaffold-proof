using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TaskFlow.Bootstrapper.HealthChecks;

/// <summary>Configures service bus health check host behavior for TaskFlow runtime services.</summary>
public sealed class ServiceBusHealthCheck(
    IAzureClientFactory<ServiceBusClient> clientFactory,
    IConfiguration config) : IHealthCheck
{
    /// <summary>Provides the check health operation for service bus health check.</summary>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = clientFactory.CreateClient("TaskFlowSBClient");
            var topic = config["DomainEventsTopic"] ?? "DomainEvents";
            await using var sender = client.CreateSender(topic);
            using var batch = await sender.CreateMessageBatchAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Service Bus connection failed.", ex);
        }
    }
}
