using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TaskFlow.Application.Contracts.Storage;

namespace TaskFlow.Bootstrapper;

/// <summary>Read-model backend selected for this deployment (D-038).</summary>
public enum ReadModelProvider
{
    /// <summary>The existing denormalized Cosmos DB TaskView projection.</summary>
    Cosmos,

    /// <summary>A relational TaskView table. Not implemented yet (slice P3).</summary>
    Relational
}

public static partial class RegisterServices
{
    public const string ReadModelProviderConfigKey = "ReadModel:Provider";
    public const string ReadModelProviderEnvVar = "TASKFLOW_READMODEL_PROVIDER";

    /// <summary>
    /// Resolves the read-model backend. The environment variable wins over configuration; when neither is
    /// set, the Portable lane defaults to Relational and the Azure lane keeps today's Cosmos default (D-035).
    /// </summary>
    public static ReadModelProvider ResolveReadModelProvider(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var value = Environment.GetEnvironmentVariable(ReadModelProviderEnvVar) ?? config[ReadModelProviderConfigKey];
        if (!string.IsNullOrWhiteSpace(value)) return ParseReadModelProvider(value);

        return HostingLaneSelector.Resolve(config) == HostingLane.Portable
            ? ReadModelProvider.Relational
            : ReadModelProvider.Cosmos;
    }

    private static ReadModelProvider ParseReadModelProvider(string value) =>
        Enum.TryParse<ReadModelProvider>(value, ignoreCase: true, out var provider)
            ? provider
            : throw new ArgumentException(
                $"Unknown read-model provider '{value}'. Allowed values: {string.Join(", ", Enum.GetNames<ReadModelProvider>())}.");

    /// <summary>Dispatches to the selected read-model backend.</summary>
    [ProviderSwitch(typeof(ITaskViewRepository))]
    private static void AddReadModelServices(IServiceCollection services, IConfiguration config)
    {
        switch (ResolveReadModelProvider(config))
        {
            case ReadModelProvider.Cosmos:
                AddCosmosDbServices(services, config);
                break;
            case ReadModelProvider.Relational:
                throw new NotSupportedException("ReadModel provider Relational is not implemented yet (slice P3).");
        }
    }
}
