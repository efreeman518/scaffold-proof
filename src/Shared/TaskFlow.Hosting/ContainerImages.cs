namespace TaskFlow.Hosting;

/// <summary>Single image-name and tag catalog for local lane infrastructure (D-060).</summary>
public static class ContainerImages
{
    public const string MicrosoftContainerRegistry = "mcr.microsoft.com";

    public const string SqlServerRepository = "mssql/server";
    public const string SqlServerTag = "2025-CU8-ubuntu-22.04";
    public const string SqlServer = $"{MicrosoftContainerRegistry}/{SqlServerRepository}:{SqlServerTag}";

    public const string ServiceBusEmulatorRepository = "azure-messaging/servicebus-emulator";
    public const string ServiceBusEmulatorTag = "2.0.1@sha256:5a96d893b245031740f7d46e0fe5ff282d24b78c4b7d761dd57590f3f010a9b3";
    public const string ServiceBusEmulator = $"{MicrosoftContainerRegistry}/{ServiceBusEmulatorRepository}:{ServiceBusEmulatorTag}";

    public const string ServiceBusSqlServerRepository = "mssql/server";
    // MCR publishes CU27 only as linux/amd64, not a multi-arch index. Replace this manifest digest when MCR adds arm64.
    public const string ServiceBusSqlServerTag = "2022-CU27-ubuntu-22.04@sha256:4402d880dd4c34bfa7d8705e56a86cd6c88da80a1f6bbbe741f999e76264a090";
    public const string ServiceBusSqlServer = $"{MicrosoftContainerRegistry}/{ServiceBusSqlServerRepository}:{ServiceBusSqlServerTag}";

    public const string AzuriteRepository = "azure-storage/azurite";
    public const string AzuriteTag = "3.37.0@sha256:830430c1da1a2d537e08f3e6764dd1f5ae00cf0346bcaf625b968ec3f0971fd5";
    public const string Azurite = $"{MicrosoftContainerRegistry}/{AzuriteRepository}:{AzuriteTag}";

    public const string CosmosEmulatorRepository = "cosmosdb/linux/azure-cosmos-emulator";
    public const string CosmosEmulatorTag = "vnext-EN20260907@sha256:2db1f9e74c506bcf6fc347aa937aea1c00fa756061296a5a9efba530ce86ec02";
    public const string CosmosEmulator = $"{MicrosoftContainerRegistry}/{CosmosEmulatorRepository}:{CosmosEmulatorTag}";

    public const string PostgreSqlRepository = "pgvector/pgvector";
    public const string PostgreSqlTag = "0.8.6-pg18@sha256:2ba9ca5f2e7daa0f0e7723cba1ee9167bab54efd3640516a44ac1a928dd67e7a";
    public const string PostgreSql = $"{PostgreSqlRepository}:{PostgreSqlTag}";

    public const string RabbitMqRepository = "rabbitmq";
    public const string RabbitMqTag = "4.3.6-management@sha256:5935b8b172f3351664b7f1610a109b3c883cec000bebeeca894d1719d18ffc76";
    public const string RabbitMq = $"{RabbitMqRepository}:{RabbitMqTag}";

    public const string SeaweedFsRepository = "chrislusf/seaweedfs";
    public const string SeaweedFsTag = "4.47@sha256:ce9e796f1fe6f06968f4c04bdaf8f678dad9c8acdfef3d244133d71bfa6bf882";
    public const string SeaweedFs = $"{SeaweedFsRepository}:{SeaweedFsTag}";

    public const string MongoDbRepository = "mongo";
    public const string MongoDbTag = "8.3.11@sha256:2609aaf7a1abbff404101af896e05f243d22be742471ed857b50b9ce0270fdbd";
    public const string MongoDb = $"{MongoDbRepository}:{MongoDbTag}";

    public const string RedisRepository = "redis";
    public const string RedisTag = "8.8.2@sha256:37227fff5638322f4ebea25d6d0dc3ee50848604e82b81426f11507b3ec7d2cc";
    public const string Redis = $"{RedisRepository}:{RedisTag}";
}
