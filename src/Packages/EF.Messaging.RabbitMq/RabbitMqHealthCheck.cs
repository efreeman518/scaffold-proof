using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;

namespace EF.Messaging.RabbitMq;

/// <summary>
/// Reports Healthy while the process connection is open, and the registration's failure status once the
/// connection is closed or the broker cannot be reached.
/// </summary>
internal sealed class RabbitMqHealthCheck(IRabbitMqConnectionMultiplexer multiplexer) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            IConnection connection = await multiplexer.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
            return connection.IsOpen
                ? HealthCheckResult.Healthy($"Connected to {connection.Endpoint}.")
                : new HealthCheckResult(context.Registration.FailureStatus, "The RabbitMQ connection is closed.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "The RabbitMQ broker could not be reached.", ex);
        }
    }
}
