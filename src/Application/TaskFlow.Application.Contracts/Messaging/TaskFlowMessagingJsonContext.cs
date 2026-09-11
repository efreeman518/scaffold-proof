using EF.Messaging;
using System.Text.Json.Serialization;
using TaskFlow.Domain.Shared;
using TaskFlow.Domain.Shared.Events;

namespace TaskFlow.Application.Contracts.Messaging;

/// <summary>
/// D-048: source-generated serialization for the messaging wire shapes. Separate from
/// <c>TaskFlow.Application.Models.Serialization.TaskFlowJsonContext</c> for two reasons, both structural:
/// <list type="bullet">
/// <item><see cref="IntegrationEventEnvelope"/> is the messaging wire shape, and Application.Contracts
/// references Application.Models, not the other way round - a single context would mean an inverted
/// reference.</item>
/// <item>The broker payload is a cross-service contract serialized with default (PascalCase) naming, while
/// the HTTP context declares camelCase. These attributes apply whenever the context is used directly
/// (<c>Default.IntegrationEventEnvelope</c>), which is every messaging call site, so the naming has to be
/// the messaging naming. Declaring no <c>PropertyNamingPolicy</c> keeps the format byte-identical to what
/// the reflection serializer produced.</item>
/// </list>
/// <c>PropertyNameCaseInsensitive</c> is on so a payload written by an older or newer build (either naming)
/// still deserializes; a message already in a queue during a rolling deploy must not become poison.
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(IntegrationEventEnvelope))]
// Payload records. TaskFlowIntegrationEvents.Envelope serializes the raised event through this context, so
// the set here must match TaskFlowIntegrationEvents.Versions - an event type missing from either is dropped
// (unknown type) or falls back to reflection.
[JsonSerializable(typeof(IDomainEvent))]
[JsonSerializable(typeof(TaskItemCreatedEvent))]
[JsonSerializable(typeof(TaskItemContentChangedEvent))]
[JsonSerializable(typeof(TaskItemStatusChangedEvent))]
[JsonSerializable(typeof(TaskItemCompletedEvent))]
[JsonSerializable(typeof(TaskItemOverdueSuspectedEvent))]
[JsonSerializable(typeof(TaskItemRescheduledEvent))]
[JsonSerializable(typeof(CommentAddedEvent))]
[JsonSerializable(typeof(AttachmentUploadedEvent))]
public partial class TaskFlowMessagingJsonContext : JsonSerializerContext;
