namespace TaskFlow.Hosting;

/// <summary>Single image-name and tag catalog for local lane infrastructure (D-060).</summary>
public static class ContainerImages
{
    public const string MicrosoftContainerRegistry = "mcr.microsoft.com";

    public const string SqlServerRepository = "mssql/server";
    public const string SqlServerTag = "2025-latest";
    public const string SqlServer = $"{MicrosoftContainerRegistry}/{SqlServerRepository}:{SqlServerTag}";

    public const string ServiceBusEmulatorRepository = "azure-messaging/servicebus-emulator";
    public const string ServiceBusEmulatorTag = "latest";
    public const string ServiceBusEmulator = $"{MicrosoftContainerRegistry}/{ServiceBusEmulatorRepository}:{ServiceBusEmulatorTag}";

    public const string ServiceBusSqlServerRepository = "mssql/server";
    public const string ServiceBusSqlServerTag = "2022-latest";
    public const string ServiceBusSqlServer = $"{MicrosoftContainerRegistry}/{ServiceBusSqlServerRepository}:{ServiceBusSqlServerTag}";

    public const string AzuriteRepository = "azure-storage/azurite";
    public const string AzuriteTag = "latest";
    public const string Azurite = $"{MicrosoftContainerRegistry}/{AzuriteRepository}:{AzuriteTag}";

    public const string CosmosEmulatorRepository = "cosmosdb/linux/azure-cosmos-emulator";
    public const string CosmosEmulatorTag = "vnext-latest";
    public const string CosmosEmulator = $"{MicrosoftContainerRegistry}/{CosmosEmulatorRepository}:{CosmosEmulatorTag}";

    public const string PostgreSqlRepository = "pgvector/pgvector";
    public const string PostgreSqlTag = "pg18";
    public const string PostgreSql = $"{PostgreSqlRepository}:{PostgreSqlTag}";

    public const string RabbitMqRepository = "rabbitmq";
    public const string RabbitMqTag = "4-management";
    public const string RabbitMq = $"{RabbitMqRepository}:{RabbitMqTag}";

    public const string SeaweedFsRepository = "chrislusf/seaweedfs";
    public const string SeaweedFsTag = "latest";
    public const string SeaweedFs = $"{SeaweedFsRepository}:{SeaweedFsTag}";

    public const string MongoDbRepository = "mongo";
    public const string MongoDbTag = "8";
    public const string MongoDb = $"{MongoDbRepository}:{MongoDbTag}";

    public const string RedisRepository = "redis";
    public const string RedisTag = "8";
    public const string Redis = $"{RedisRepository}:{RedisTag}";
}
