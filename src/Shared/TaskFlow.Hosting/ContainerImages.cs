namespace TaskFlow.Hosting;

/// <summary>Single image-name and tag catalog for local lane infrastructure (D-060).</summary>
public static class ContainerImages
{
    public const string SqlServer = "mcr.microsoft.com/mssql/server:2025-latest";
    public const string ServiceBusEmulator = "mcr.microsoft.com/azure-messaging/servicebus-emulator:latest";
    public const string ServiceBusSqlServer = "mcr.microsoft.com/mssql/server:2022-latest";
    public const string Azurite = "mcr.microsoft.com/azure-storage/azurite:latest";
    public const string CosmosEmulator = "mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-latest";
    public const string PostgreSql = "pgvector/pgvector:pg18";
    public const string RabbitMq = "rabbitmq:4-management";
    public const string SeaweedFs = "chrislusf/seaweedfs:latest";
    public const string MongoDb = "mongo:8";
    public const string Redis = "redis:8";
}
