using EF.Domain.Contracts;
using EF.Testing.Architecture;
using TaskFlow.Domain.Shared;

namespace Test.Architecture;

/// <summary>
/// Reflection-based convention checks: every domain entity implements <c>ITenantEntity&lt;TenantId&gt;</c>,
/// every <c>*Service</c> implementation has a matching <c>I*Service</c> interface, and entity properties
/// (excluding <c>TenantId</c>) have private setters to enforce encapsulation.
/// Pure-unit tier (EF.Testing.Architecture/reflection only): inspects loaded type metadata; no runtime, no infra.
/// </summary>
[TestClass]
[TestCategory("Architecture")]
public class ConventionTests : BaseTest
{
    private static readonly Type[] KnownEntities =
    [
        typeof(TaskFlow.Domain.Model.Category),
        typeof(TaskFlow.Domain.Model.Tag),
        typeof(TaskFlow.Domain.Model.TaskItem),
        typeof(TaskFlow.Domain.Model.Comment),
        typeof(TaskFlow.Domain.Model.ChecklistItem),
        typeof(TaskFlow.Domain.Model.Attachment),
        typeof(TaskFlow.Domain.Model.TaskItemTag)
    ];

    /// <summary>Verifies that given domain entities, when checked, then all implement i tenant entity.</summary>
    [TestMethod]
    public void Given_DomainEntities_When_Checked_Then_AllImplementITenantEntity()
    {
        var tenantInterface = typeof(ITenantEntity<TenantId>);
        var nonTenantEntities = KnownEntities
            .Where(e => !tenantInterface.IsAssignableFrom(e))
            .ToList();

        Assert.IsEmpty(nonTenantEntities,
            $"Entities missing ITenantEntity<TenantId>: {string.Join(", ", nonTenantEntities.Select(t => t.Name))}");
    }

    /// <summary>Verifies that given service implementations, when checked, then all implement their interface.</summary>
    [TestMethod]
    public void Given_ServiceImplementations_When_Checked_Then_AllImplementTheirInterface()
    {
        // An assembly with no *Service class is a violation too ("no types matched"), so this cannot pass vacuously.
        var result = ConventionRules.ClassesImplementMatchingInterface(ApplicationServicesAssembly, "Service");

        Assert.IsTrue(result.IsSuccessful, $"Service convention violations: {result}");
    }

    /// <summary>Verifies that given domain entities, when checked, then all have private setters.</summary>
    [TestMethod]
    public void Given_DomainEntities_When_Checked_Then_AllHavePrivateSetters()
    {
        // TenantId is excluded - public setter required by ITenantEntity<TenantId> interface contract
        var result = ConventionRules.NoPublicSetters(KnownEntities, exemptPropertyNames: ["TenantId"]);

        Assert.IsTrue(result.IsSuccessful, $"Entities with public setters (encapsulation violation): {result}");
    }

    /// <summary>
    /// Files that already declared several public top-level types when the one-public-type-per-file rule was
    /// adopted: co-located families (CQRS request records, strongly typed ids, provider enums beside their partial
    /// registration, a DTO beside its paging shape, the hand-authored Uno API client). The list is a ratchet: new
    /// files follow the rule, and an entry that no longer violates fails the stale check below and must be removed.
    /// </summary>
    private static readonly string[] MultiTypeFileAllowList =
    [
        "src/Application/TaskFlow.Application.Contracts/ApplicationStyle.cs",
        "src/Application/TaskFlow.Application.Contracts/AuthMode.cs",
        "src/Application/TaskFlow.Application.Contracts/Caching/TaskFlowCache.cs",
        "src/Application/TaskFlow.Application.Contracts/Repositories/ITaskEmbeddingRepository.cs",
        "src/Application/TaskFlow.Application.Contracts/Repositories/ITaskItemSystemRepository.cs",
        "src/Application/TaskFlow.Application.Contracts/Services/ITaskViewProjectionService.cs",
        "src/Application/TaskFlow.Application.Contracts/Storage/ITaskViewRepository.cs",
        "src/Application/TaskFlow.Application.Cqrs/Features/Attachments/AttachmentRequests.cs",
        "src/Application/TaskFlow.Application.Cqrs/Features/Categories/CategoryRequests.cs",
        "src/Application/TaskFlow.Application.Cqrs/Features/ChecklistItems/ChecklistItemRequests.cs",
        "src/Application/TaskFlow.Application.Cqrs/Features/Comments/CommentRequests.cs",
        "src/Application/TaskFlow.Application.Cqrs/Features/Tags/TagRequests.cs",
        "src/Application/TaskFlow.Application.Cqrs/Features/TaskItems/TaskItemChildRequests.cs",
        "src/Application/TaskFlow.Application.Cqrs/Features/TaskItems/TaskItemRequests.cs",
        "src/Application/TaskFlow.Application.MessageHandlers/Consumers/IntegrationEventConsumers.cs",
        "src/Application/TaskFlow.Application.MessageHandlers/WorkflowTriggerHandler.cs",
        "src/Application/TaskFlow.Application.Models/Reads/TaskItemSummaryDto.cs",
        "src/Domain/TaskFlow.Domain.Shared/Ids/DomainIds.cs",
        "src/Host/Aspire/AppHost/LaneDefaults.cs",
        "src/Host/TaskFlow.Bootstrapper/Registration/RegisterServices.AiChatClient.cs",
        "src/Host/TaskFlow.Bootstrapper/Registration/RegisterServices.Audit.cs",
        "src/Host/TaskFlow.Bootstrapper/Registration/RegisterServices.Messaging.cs",
        "src/Host/TaskFlow.Bootstrapper/Registration/RegisterServices.ReadModel.cs",
        "src/Host/TaskFlow.Bootstrapper/Registration/RegisterServices.Storage.cs",
        "src/Host/TaskFlow.DatabaseMigrator/DesignTimeDbContextFactories.cs",
        "src/Infrastructure/TaskFlow.Infrastructure.AI/Agents/AgentModels.cs",
        "src/Infrastructure/TaskFlow.Infrastructure.AI/Demos/AiDemoModels.cs",
        "src/Infrastructure/TaskFlow.Infrastructure.AI/Demos/NextActionAdvisor.cs",
        "src/Infrastructure/TaskFlow.Infrastructure.AI/Demos/TaskDraftService.cs",
        "src/Infrastructure/TaskFlow.Infrastructure.AI/Demos/TaskTriageService.cs",
        "src/Infrastructure/TaskFlow.Infrastructure.AI/Search/TaskItemSearchResult.cs",
        "src/Infrastructure/TaskFlow.Infrastructure.AI/ServiceCollectionExtensions.cs",
        "src/Infrastructure/TaskFlow.Infrastructure.Data/Provider/TaskFlowDbProvider.cs",
        "src/Infrastructure/TaskFlow.Infrastructure.Data/ReadModel/TaskViewRecord.cs",
        "src/Infrastructure/TaskFlow.Infrastructure.Repositories/MongoDb/MongoTaskViewRepository.cs",
        "src/Infrastructure/TaskFlow.Infrastructure.Repositories/TaskFlowGenericRepositories.cs",
        "src/Shared/TaskFlow.Hosting/HostingLane.cs",
        "src/UI/TaskFlow.Uno.Core/Client/TaskFlowApiClient.cs",
        "src/UI/TaskFlow.Uno.Presentation/Presentation/IFormGuard.cs"
    ];

    /// <summary>
    /// Every file under <c>src/</c> outside the allow-list declares at most one public top-level type, so a public
    /// type is found by its file name. Nested and file-scoped types do not count.
    /// </summary>
    [TestMethod]
    public void Given_SourceTree_When_Parsed_Then_OnePublicTypePerFile()
    {
        var result = SourceRules.OnePublicTypePerFile(RepoFiles.Root, RepoFiles.SourceFiles, MultiTypeFileAllowList);
        var stale = SourceRules.StaleAllowListEntries(
            SourceRules.OnePublicTypePerFile(RepoFiles.Root, RepoFiles.SourceFiles), MultiTypeFileAllowList);

        Assert.IsTrue(result.IsSuccessful, result.ToString());
        Assert.IsEmpty(stale, "Remove these allow-list entries; they now declare one public type: " + string.Join(", ", stale));
    }
}
