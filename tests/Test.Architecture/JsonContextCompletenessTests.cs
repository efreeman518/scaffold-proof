using EF.Common.Contracts;
using System.Reflection;
using System.Text;
using TaskFlow.Application.Contracts.Messaging;
using TaskFlow.Application.MessageHandlers.Consumers;
using TaskFlow.Application.Models;
using TaskFlow.Application.Models.Paging;
using TaskFlow.Application.Models.Serialization;
using TaskFlow.Domain.Shared;
using TaskFlow.Domain.Shared.Events;
using TaskFlow.Infrastructure.Data.Interceptors;

namespace Test.Architecture;

/// <summary>
/// Guards D-048 completeness. A DTO missing from <see cref="TaskFlowJsonContext"/> costs nothing at build
/// time and nothing visible at run time - the reflection resolver kept behind the generated one answers for
/// it - so the only symptom is the per-request metadata build that source generation was added to remove.
/// That silence is exactly why this is a test.
/// Pure-unit tier (reflection over the loaded assemblies): no DI, I/O, or host.
/// </summary>
[TestClass]
[TestCategory("Architecture")]
public class JsonContextCompletenessTests
{
    /// <summary>Suffixes that mark a type as part of an HTTP or cached payload.</summary>
    private static readonly string[] PayloadSuffixes = ["Dto", "Request", "Response", "Page"];

    /// <summary>Verifies every payload-shaped public type in Application.Models resolves through the context.</summary>
    [TestMethod]
    public void Given_ApplicationModelsPayloadTypes_When_ResolvedThroughContext_Then_AllHaveGeneratedMetadata()
    {
        var missing = PayloadTypes()
            .Where(t => TaskFlowJsonContext.Default.GetTypeInfo(t) is null)
            .Select(t => t.FullName!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.AreEqual(0, missing.Count,
            "These Application.Models payload types have no generated metadata and fall back to the "
            + $"reflection resolver (D-048). Add a [JsonSerializable] entry for each: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// Verifies the closed generic shapes the endpoints actually return. No assembly scan can find these:
    /// a generic type definition has no JsonTypeInfo, so the list in the context is the only record of them
    /// and this asserts the list and the attributes have not drifted apart.
    /// </summary>
    [TestMethod]
    public void Given_RegisteredClosedGenerics_When_ResolvedThroughContext_Then_AllHaveGeneratedMetadata()
    {
        Assert.IsTrue(TaskFlowJsonContext.RegisteredClosedGenerics.Length > 0,
            "The closed-generic list must not be empty; an empty list makes this test vacuous.");

        var missing = TaskFlowJsonContext.RegisteredClosedGenerics
            .Where(t => TaskFlowJsonContext.Default.GetTypeInfo(t) is null)
            .Select(t => t.Name)
            .ToList();

        Assert.AreEqual(0, missing.Count,
            "TaskFlowJsonContext.RegisteredClosedGenerics names shapes with no [JsonSerializable] attribute: "
            + string.Join(", ", missing));
    }

    /// <summary>
    /// Verifies the detector is not vacuous. If GetTypeInfo answered for everything - a chained reflection
    /// resolver, a future generator change - the two tests above would pass forever regardless of the
    /// attributes, so an intentionally unregistered closure must come back null.
    /// </summary>
    [TestMethod]
    public void Given_AnUnregisteredClosure_When_ResolvedThroughContext_Then_IsNull()
    {
        // Only CursorPage<TaskItemDto> is registered; the TaskItem list is the one cursor-paged read.
        Assert.IsNull(TaskFlowJsonContext.Default.GetTypeInfo(typeof(CursorPage<CategoryDto>)),
            "TaskFlowJsonContext resolved a type it never declared, so the completeness tests above prove "
            + "nothing. Check whether a reflection resolver has been chained into the generated context.");
    }

    /// <summary>
    /// Verifies the messaging context covers exactly the event types the envelope claims to know. The two
    /// lists are independent declarations of the same fact, and a payload record present in one but not the
    /// other means either an event that cannot be consumed or one that silently serializes by reflection.
    /// </summary>
    [TestMethod]
    public void Given_DomainEventRecords_When_ResolvedThroughMessagingContext_Then_MatchesKnownEventTypes()
    {
        var eventTypes = typeof(IDomainEvent).Assembly.GetExportedTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IDomainEvent).IsAssignableFrom(t))
            .ToList();

        Assert.IsTrue(eventTypes.Count > 0, "No domain event records found; the scan must be wrong.");

        foreach (var eventType in eventTypes)
        {
            var generated = TaskFlowMessagingJsonContext.Default.GetTypeInfo(eventType) is not null;
            var known = TaskFlowIntegrationEvents.IsKnownType(eventType.Name);

            Assert.AreEqual(known, generated,
                $"{eventType.Name} is {(known ? "listed in" : "absent from")} "
                + $"TaskFlowIntegrationEvents.Versions but {(generated ? "is" : "is not")} registered on "
                + "TaskFlowMessagingJsonContext. TaskFlowIntegrationEvents.Envelope resolves the payload "
                + "through that context and throws when it is missing, so the two lists have to agree.");
        }
    }

    /// <summary>Verifies the envelope itself - the outbox row payload and every broker body - is generated.</summary>
    [TestMethod]
    public void Given_IntegrationEventEnvelope_When_ResolvedThroughMessagingContext_Then_HasGeneratedMetadata() =>
        Assert.IsNotNull(TaskFlowMessagingJsonContext.Default.IntegrationEventEnvelope,
            "The envelope is serialized inside SaveChanges on every event-raising write and deserialized on "
            + "every delivery; it is the one shape that must never fall back to reflection.");

    /// <summary>
    /// Verifies the outbox row payload keeps the PascalCase wire format the reflection serializer produced,
    /// and still parses through the shared reader. This is the one D-048 decision with a blast radius
    /// outside the process: the row is read by the Service Bus triggers and the RabbitMQ handlers, and a
    /// naming change would turn every message already in a queue during a rolling deploy into poison.
    /// </summary>
    [TestMethod]
    public void Given_StagedOutboxRow_When_Serialized_Then_KeepsPascalCaseAndRoundTrips()
    {
        var tenantId = Guid.CreateVersion7();
        var raised = new TaskItemCreatedEvent(Guid.CreateVersion7(), tenantId, "guarded");
        var envelope = TaskFlowIntegrationEvents.Envelope(raised, DateTimeOffset.UtcNow, correlationId: null);
        var row = OutboxStagingInterceptor.ToRow(envelope, tenantId, DateTimeOffset.UtcNow);

        StringAssert.Contains(row.Payload, "\"Type\":",
            "The envelope must stay PascalCase on the wire; a camelCase context here would break every "
            + "in-flight message across a rolling deploy.");
        StringAssert.Contains(row.Payload, "\"Title\":",
            "The payload record must stay PascalCase too - it is serialized through the messaging context.");

        Assert.IsTrue(
            IntegrationEnvelopeReader.TryRead(Encoding.UTF8.GetBytes(row.Payload), out var read, out var failure),
            $"The staged payload must parse back through the shared reader; got {failure}.");
        Assert.AreEqual(envelope.Id, read!.Id);
        Assert.AreEqual(nameof(TaskItemCreatedEvent), read.Type);
    }

    private static IEnumerable<Type> PayloadTypes() =>
        typeof(TaskItemDto).Assembly.GetExportedTypes()
            .Where(t => t is { IsGenericTypeDefinition: false, IsInterface: false, IsEnum: false })
            .Where(t => PayloadSuffixes.Any(s => t.Name.EndsWith(s, StringComparison.Ordinal)))
            // Nested payload closures such as DefaultResponse`1 are covered by the closed-generic test.
            .Where(t => !t.ContainsGenericParameters);

    /// <summary>Keeps the reflection helper honest about which assembly it scans.</summary>
    [TestMethod]
    public void Given_ApplicationModelsAssembly_When_Scanned_Then_FindsPayloadTypes()
    {
        var found = PayloadTypes().Select(t => t.Name).ToList();

        CollectionAssert.Contains(found, nameof(TaskItemDto));
        CollectionAssert.Contains(found, nameof(TaskItemCursorSearchRequest));
        Assert.IsTrue(found.Count >= 10,
            $"Only {found.Count} payload types found in {typeof(TaskItemDto).Assembly.GetName().Name}; the "
            + "scan is probably pointed at the wrong assembly.");
    }

    /// <summary>Documents which assembly the scan is anchored on, for a reader of a failure message.</summary>
    [TestMethod]
    public void Given_ContextAndModels_When_Compared_Then_LiveInTheSameAssembly() =>
        Assert.AreSame(typeof(TaskItemDto).Assembly, typeof(TaskFlowJsonContext).Assembly,
            "TaskFlowJsonContext must ship in TaskFlow.Application.Models, or the completeness scan above "
            + $"covers the wrong assembly: {typeof(TaskFlowJsonContext).Assembly.GetName().Name}.");

    [TestMethod]
    public void Given_FunctionsWorkerJson_When_Configured_Then_UsesWebDefaults()
    {
        var source = File.ReadAllText(RepoFiles.Path(
            "src", "Host", "TaskFlow.Functions", "Program.cs"));

        StringAssert.Contains(source, "new JsonSerializerOptions(JsonSerializerDefaults.Web)",
            "Functions HTTP payloads must remain camelCase and case-insensitive like the API and generated clients.");
    }
}
