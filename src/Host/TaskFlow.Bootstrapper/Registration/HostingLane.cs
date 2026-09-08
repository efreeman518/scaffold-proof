global using HostingLane = TaskFlow.Application.Contracts.Configuration.HostingLane;
global using HostingLaneSelector = TaskFlow.Application.Contracts.Configuration.HostingLaneSelector;
using TaskFlow.Infrastructure.AI;
using TaskFlow.Infrastructure.Data.Provider;

namespace TaskFlow.Bootstrapper;

/// <summary>
/// Per-switch defaults seeded by the hosting lane (D-035). The lane itself is resolved once by
/// <see cref="HostingLaneSelector"/> (owned by Application.Contracts - the lowest project both
/// Infrastructure.Data and Infrastructure.AI already reference, so the Database and Search provider
/// selectors share the exact same parsing without either pulling in a sibling Infrastructure project);
/// every switch below reuses that resolver rather than reading <c>TASKFLOW_LANE</c>/<c>Hosting:Lane</c>
/// itself. A switch's own explicit env var or config key always wins over its lane default - the lane
/// only seeds the fallback used when neither is set. Azure lane values are not listed: they equal each
/// switch's pre-existing hard default (including the dynamic ones - AI and Search derive from other
/// settings, DataProtection from the blob URL), so "Azure" means "today's behavior", not a new fixed value.
/// </summary>
public static class LaneDefaults
{
    /// <summary>Defaults applied when no switch-specific env var or config key is set and the lane is Portable.</summary>
    public static class Portable
    {
        public const TaskFlowDbProvider Database = TaskFlowDbProvider.PostgreSql;
        public const MessagingProvider Messaging = MessagingProvider.RabbitMq;
        public const StorageProvider Storage = StorageProvider.S3;
        public const ReadModelProvider ReadModel = ReadModelProvider.Relational;
        public const AuditProvider Audit = AuditProvider.Relational;
        public const SearchProvider Search = SearchProvider.Sql;
        public const AiProvider AiServices = AiProvider.OpenAICompatible;
        public const DataProtectionPersistence DataProtection = DataProtectionPersistence.Redis;
    }
}
