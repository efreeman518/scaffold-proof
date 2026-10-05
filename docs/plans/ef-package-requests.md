# EF.* Package Change Requests

Open requests from TaskFlow (scaffold-proof) to the EF.* platform packages (EF.Packages repo) and EF.FlowEngine. Every earlier request has landed and been adopted; TaskFlow carries no app-local fallback for a package gap (`// fallback:` appears nowhere in `src/` or `tests/`). Each entry: package, ask, why, acceptance, and the trigger that would make it worth building.

## Deferred - build when a consumer needs it

**(d) EF.Messaging.Contracts `IntegrationEventEnvelope`: tenant slot.** The envelope is `(Id, Type, Version, OccurredAtUtc, CorrelationId, Payload)`, so a multi-tenant producer puts the tenant in the payload and a broker header, and a consumer cannot route or filter by tenant before deserializing the body. TaskFlow does exactly that (`TenantId` in the payload and the `TenantId` broker header) and needs nothing more. Acceptance: an optional `TenantId` on the envelope, written by `IntegrationEventEnvelope.From` and the transports, read by `EnvelopeSerializer`. Trigger: a consumer that must route, filter or partition by tenant before reading the payload.

**(e) EF.Messaging `ServiceBusSenderPool`: enumeration and disposal.** It exposes only `Count` and `Get` and is not `IAsyncDisposable`, so a consumer cannot list open senders or close one without disposing the whole client. TaskFlow does not use the pool. Acceptance: `IAsyncDisposable` that closes every sender, plus a read-only view of the open entity names. Trigger: a host that needs to close senders on shutdown independently of the `ServiceBusClient`, or to report them.

**(g) EF.Messaging.Contracts `MessagingTraceContext`: W3C Baggage.** It propagates `traceparent`/`tracestate` but not `baggage`, so `Activity.Baggage` is lost across a message boundary. TaskFlow correlates through the trace and `CorrelationId` and reads no Baggage. Acceptance: Inject writes a `baggage` header from `Activity.Current`, Extract restores it, bounded to the W3C size limit. Trigger: a consumer that relies on Baggage for cross-service correlation.

## EF.FlowEngine (separate repository, not verifiable from EF.Packages)

**17 - `Clients.Sql.AdHocSqlQueryClient` split into `Clients.SqlServer`, and README guidance for `UseRetentionPolicy`.** The README guidance was last seen half done and the split unconfirmed. TaskFlow does not use the ad hoc SQL client. Verify against the current EF.FlowEngine release before acting.

**19 - engine-stable UUIDv7 per loop iteration.** Declined for now (D-059): `LoopNodeExecutor.IterationId` is a deterministic UUIDv5 and GR-17 rejects a non-UUIDv7 client create id with 400, so the decomposer loop keeps its `idempotencyKey`. `WorkflowDefinitionValidityTests.LoopIterationId_IsStillAVersion5Uuid_PackageRequest19` (Test.Integration.FlowEngine) calls the internal `LoopNodeExecutor.IterationId` by reflection and asserts version 5, so a FlowEngine release that changes it fails CI and prompts adoption.
