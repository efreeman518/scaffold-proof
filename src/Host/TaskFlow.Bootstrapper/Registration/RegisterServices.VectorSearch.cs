using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.MessageHandlers.Consumers;
using TaskFlow.Infrastructure.AI;
using TaskFlow.Infrastructure.Repositories;

namespace TaskFlow.Bootstrapper;

/// <summary>Composition of the pgvector search projection (D-040).</summary>
public static partial class RegisterServices
{
    /// <summary>
    /// Registers the embedding storage port and its consumer only on the PgVector arm. On every other arm
    /// nothing is registered, no queue or subscription is declared for it (see the RabbitMQ topology and
    /// AppHost/Bicep), and therefore nothing accumulates unread.
    /// <para>
    /// The fail-fast checks that make the arm safe (PostgreSQL required, embedding generator required) live
    /// in <c>AddAiServices</c> beside the switch itself; this only follows the same resolved provider.
    /// </para>
    /// </summary>
    private static void AddVectorSearchServices(IServiceCollection services, IConfiguration config)
    {
        if (AiServiceCollectionExtensions.ResolveSearchProvider(config) != SearchProvider.PgVector) return;

        services.AddScoped<ITaskEmbeddingRepository, PgVectorTaskEmbeddingRepository>();
        services.AddScoped<TaskEmbeddingConsumer>();
    }
}
