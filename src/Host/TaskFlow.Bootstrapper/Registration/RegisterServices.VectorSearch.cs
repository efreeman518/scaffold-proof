using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.MessageHandlers.Consumers;
using TaskFlow.Infrastructure.AI;
using TaskFlow.Infrastructure.Data.Provider;
using TaskFlow.Infrastructure.Repositories;

namespace TaskFlow.Bootstrapper;

/// <summary>Composition of the pgvector search projection (D-040).</summary>
public static partial class RegisterServices
{
    /// <summary>
    /// Registers the embedding storage port and its consumer only on the PgVector arm, and enforces the arm's
    /// database prerequisite. On every other arm nothing is registered, no queue or subscription is declared
    /// for it (see the RabbitMQ topology and AppHost/Bicep), and therefore nothing accumulates unread.
    /// <para>
    /// The PostgreSQL check lives here, not in <c>AddAiServices</c>, because only the Bootstrapper composes
    /// both switches: asking <see cref="TaskFlowDbProviderSelector"/> from Infrastructure.AI would make an AI
    /// adapter depend on the data layer. It runs before <c>AddAiServices</c> (see
    /// <c>AddApplicationServices</c>), so the clearer of the two failures is the one a misconfigured
    /// deployment sees first. The remaining prerequisite - an embedding generator - stays beside the arm.
    /// </para>
    /// </summary>
    private static void AddVectorSearchServices(IServiceCollection services, IConfiguration config)
    {
        if (AiServiceCollectionExtensions.ResolveSearchProvider(config) != SearchProvider.PgVector) return;

        var dbProvider = TaskFlowDbProviderSelector.Resolve(config);
        if (dbProvider != TaskFlowDbProvider.PostgreSql)
            throw new InvalidOperationException(
                $"{AiServiceCollectionExtensions.SearchProviderConfigKey}=PgVector requires "
                + $"{TaskFlowDbProviderSelector.ConfigurationKey}=PostgreSql; this deployment resolved {dbProvider}. "
                + "The TaskItemEmbedding table is mapped only on the Npgsql provider. A SQL Server 2025 VECTOR arm "
                + "is the intended future alternative and does not exist yet - until then use "
                + $"{AiServiceCollectionExtensions.SearchProviderConfigKey}=Sql or AzureAiSearch on SQL Server.");

        services.AddScoped<ITaskEmbeddingRepository, PgVectorTaskEmbeddingRepository>();
        services.AddScoped<TaskEmbeddingConsumer>();
    }
}
