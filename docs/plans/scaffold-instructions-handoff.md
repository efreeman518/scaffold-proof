# Scaffold Instructions Handoff - Scale Baseline / Dual Provider

Deliverable for the scaffold-ai instructions agent. Describes each pattern implemented on
`feature/scale-baseline-dual-provider`, as merged, not as planned - grep the paths named below if a
detail matters. Each pattern: problem, exact shape (files/types/signatures/config), the invariant or
test that proves it, and any provider-specific alternative documented as a `// fallback:` /
`// alternative:` comment in code.

## 1. Provider switch + provider-neutral model (D-020, D-022, D-024, D-025, D-030)

**Problem**: support SQL Server and PostgreSQL behind one EF Core model with one branch point, not
scattered `if (provider)` checks.

**Shape**: `Infrastructure.Data/Provider/TaskFlowDbProvider.cs`: `enum TaskFlowDbProvider { SqlServer,
PostgreSql }`; `record TaskFlowProviderOptions(Provider, ConnectionString, MigrationsHistoryTable,
MigrationsHistorySchema, MaxRetryCount=5, MaxRetryDelaySeconds=30, CommandTimeoutSeconds?,
CompatibilityLevel=170)`. `TaskFlowDbProviderSelector.Resolve(IConfiguration)`: env
`TASKFLOW_DB_PROVIDER` wins over config `Database:Provider`, default `SqlServer`.
`DbContextOptionsBuilder.UseTaskFlowProvider(options)` is the **only** runtime branch: SQL Server arm
picks `UseAzureSql` (connection string contains `database.windows.net`) or `UseSqlServer`, both set
`UseCompatibilityLevel`/`EnableRetryOnFailure`/`MigrationsHistoryTable`/`MigrationsAssembly`; Postgres
arm calls `UseNpgsql`. All four contexts (Trxn, Query, FlowEngine, TickerQ) and all four
`DatabaseMigrator` design-time factories call this one extension - nothing else checks the provider.
`OnModelCreating` never branches: composite tenant-first PK `(TenantId, Id)` (D-022) is one
`EntityBaseConfiguration.HasKey` call (SQL Server clusters it by default; Postgres gets a future
Citus/elastic-cluster distribution key for free). UTC normalization (D-024):
`Conventions/UtcDateTimeOffsetConverter.cs` registered once in `ConfigureConventions` - needed
because Npgsql `timestamptz` rejects a non-zero offset, so both providers get the converter even
though only one needs it. Migrations (D-025): two assemblies,
`TaskFlow.Infrastructure.Data.Migrations.{SqlServer,PostgreSql}`, each `Migrations/{TaskFlow,
FlowEngine,TickerQ}/`, fresh baseline, no history-compat shim.

**Proof**: `MigrationModelContractTests`/`DatabaseRegistrationTests` parameterized per provider;
`Test.Integration`/`Test.E2E` run the same assemblies twice via `TASKFLOW_TEST_DB_PROVIDER`.

**Customize**: `UseTaskFlowProvider` is the one edit point for a third provider.

## 2. App-managed Version + aggregate ETag + If-Match/412/428 (D-021, D-031, D-032)

**Problem**: a native concurrency token (`rowversion`/`xmin`) differs by provider, is opaque as an
`ETag`, and can't be exercised on the InMemory provider.

**Shape**: `Version` comes from the package: `EF.Domain.EntityBase<TId>.Version` (a `long`
implementing `EF.Domain.Contracts.IVersionedEntity`), incremented for every Modified entry by
`EF.Data.DbContextBase.SaveChangesAsync`, which sets the property's OriginalValue to the pre-increment
value so EF emits `WHERE Version = @original` (EF.* 1.1.100, package requests 1-2).
`TaskFlowEntityBase<TId>` adds only `ITimestampedEntity { DateTimeOffset CreatedAtUtc, ModifiedAtUtc; }`
(private setters), and `VersionTimestampInterceptor` (`Infrastructure.Data/Interceptors/`) stamps the
timestamps plus the insert baseline `Version = 1`, which the package does not do for Added entries. Aggregate-level ETag (D-031): only the root's
`Version` is If-Match currency; child mutations call a private `MarkAggregateChanged()` touching
`ModifiedAtUtc` so the root bumps. HTTP pipeline (`Host/TaskFlow.Api/Filters/`):
`IfMatchEndpointFilter` first - missing/blank -> **428**, unparseable -> 400, `If-Match: *` passes as
a logged wildcard override (pattern 9); `IfMatch` binds as `record struct(long? ExpectedVersion, bool
IsWildcard)`; `RequireIfMatch()` adds the filter + `ProducesProblem(412/428)`. `ETagEndpointFilter`
runs after every route (incl. GET), setting `ETag` from any `IETagCarrier` result.
`Concurrency/ConcurrencyGuard.Require(expected, current, type, id)` throws
`ConcurrencyMismatchException` on mismatch; `ConcurrencyGuard.SaveAsync` =
`SaveChangesAsync(OptimisticConcurrencyWinner.Throw)`. `DefaultExceptionHandler` (file is
`GlobalExceptionHandler.cs`) maps `ConcurrencyMismatchException`/`DbUpdateConcurrencyException` to
**412** with the current version in `ETag`, ahead of the generic 400 arm - every save-site
`catch (Exception)` must exclude `DbUpdateConcurrencyException` or the race silently degrades.
`EntityBaseDto.Version : long?` on every DTO; `DefaultResponse<T> : IETagCarrier` with
`AggregateVersion` (root ETag on child responses).

**Proof**: `ConcurrencyTokenTests` (stale save throws, provable only in-memory because Version is
app-managed); `Test.Endpoints` covers 428/412(+ETag)/`*`/malformed across both styles;
`Test.Architecture` asserts every save goes through `ConcurrencyGuard.SaveAsync` and every mutating
non-POST route carries `IfMatchRequiredMetadata`.

**Customize**: `IsConcurrencyFailure` matches `DbUpdateConcurrencyException` by `Type.FullName`
string on purpose, to keep EF Core out of `Application.Contracts`'s reference graph.

## 3. Caller-supplied UUIDv7 idempotent create (D-033)

**Problem**: retried creates must not duplicate, without a second idempotency-key table to keep
consistent.

**Shape**: `Concurrency/UuidV7.IsV7`/`ValidateCallerId` (null/empty valid, non-v7 -> 400).
`IdempotentCreateGuard.IsEquivalent(existing, incoming)` per entity, comparing caller-settable
scalars only (ignores Id/Version/TenantId/children - a documented limitation, not deep equality).
`IdempotentCreateConflictException` -> 409. Flow: validate id is v7 or absent -> existing id found ->
equivalent -> 200 + existing + `ETag` (`IsReplay=true`); divergent -> 409; else create with
`dto.Id ?? Guid.CreateVersion7()`.

**Proof**: `Test.Endpoints` covers non-v7 400, equivalent-replay 200, divergent-same-id 409, per
entity with a caller-id create path.

**Customize**: the entity row **is** the idempotency record - no table to expire or clean up.

## 4. Cursor paging contract

**Problem**: offset paging degrades under concurrent inserts (dup/skipped rows) and forces a
`COUNT(*)` per page.

**Shape**: the request, page and limits are package types (EF.* 1.1.100, package request 21):
`EF.Common.Contracts.CursorSearchRequest<TFilter,TSortMode>{ Filter, SortMode, PageSize, Cursor? }`,
`CursorPage<T>{ Items, NextCursor, HasMore }` - no Total, no page number - and `PageSizeLimits`
(1..100, default 50). App-local: `Application.Models/Paging/TaskItemCursorSearchRequest` (derives from
the generic and sets the default page size, which the package record leaves at 0) and
`TaskItemSortMode { IdAsc, DueDateAsc, DueDateDesc, ModifiedDesc, StatusThenId }`. The token is
`EF.Data.Contracts.CursorCodec` through `KeysetCursor`/`CursorPosition` (package request 27):
HMAC-SHA256 signed, schema-version byte, scope key re-checked on decode. TaskFlow's scope key is
`TaskItemRepositoryQuery.CursorScope` = `{tenantId:N}|{(int)sortMode}`, because the codec has no
sort-mode field - so tamper, cross-tenant and sort-mode mismatch all fail closed to 400
(`ERROR_CURSOR_INVALID`). `TaskItemRepositoryQuery.ApplyKeyset` stays app-local: one switch per sort
mode building the `ORDER BY`/`WHERE` shape, each arm commented with the index it must hit; null
`DueDate` sorts last; `Take(PageSize + 1)` computes `HasMore` without a second query. The package's
own pager (`KeysetPageAsync`) is not usable here - it types the tie-break key as
`Expression<Func<T,Guid>>` and every TaskFlow key is an `IDomainId<T>` struct behind a value
converter, which no Guid-typed selector can translate.

**Proof**: page-through with no dup/gap, `HasMore=false` at the end, tamper/cross-tenant/sort-mode
mismatch -> 400, one case per sort mode.

**Customize**: the cursor signing key is HKDF over the column-encryption DEK under the info label
`"TaskFlow.Cursor.v1"` (`RegisterServices.AddSharedApplicationServices`), so every replica agrees
without a second configured secret and a DEK rotation invalidates outstanding cursors. With
`Database:Encryption:Enabled=false` there is no shared secret and the key is per-process: cursors stop
validating after a restart or on a sibling replica (400, never a silent reset to page one).

## 5. Read service: summary, metadata, NDJSON export

**Problem**: dashboards/dropdowns paged the full data set with an oversized `PageSize`; no cheap
aggregate endpoint, no way to stream a tenant without holding a connection open.

**Shape**: `Contracts/Services/ITaskFlowReadService { GetTaskItemSummaryAsync, GetTaskMetadataAsync,
StreamTaskItemExportAsync(afterId, batchSize) }`, one impl (`TaskFlowReadService`) shared by both
application styles - the one documented Service/CQRS-split exception. `GET /task-items/summary`
(cached), `GET /task-metadata` (cached, replaces oversized paged fetches), `GET
/task-items/export?afterId&batchSize` (NDJSON, flushed per batch, `finally` records rows/duration
even on disconnect, own `"Export"` rate-limit policy).

**Proof**: export streams tens of thousands of rows with no dup/gap on resume; summary is one SQL
round trip (command interceptor assertion).

**Customize**: none of the three routes take a filter - add a new route for a filtered variant
rather than parameterizing these.

## 6. Transactional outbox via SaveChanges interceptor + lease claim + dispatcher (D-026)

**Problem**: events published best-effort after commit with exceptions swallowed - a crash between
commit and publish silently dropped the event.

**Shape**: `TaskItem.Create`/`TransitionStatus` raise events (`IHasDomainEvents`,
`DomainEventContainer`). `Interceptors/OutboxStagingInterceptor` converts pending events into
`OutboxMessage` rows inside the **same** `SavingChangesAsync` - a rolled-back save stages nothing.
Non-tracked paths (scheduler `ExecuteUpdate`) stage via `IOutboxStaging.Stage(envelope,
deterministicId?)`. `OperationalWorkBase { Id, TenantId, AvailableAtUtc, LeaseToken?, LeaseOwner?,
LeaseExpiresUtc?, AttemptCount, LastError, DeadLetteredAtUtc? }` backs `OutboxMessage` and
`BlobDeleteWork`. Claim (`OperationalWorkRepository`, provider-neutral EF Core, no raw SQL): select
candidate ids (lease free/expired, ordered, `Take(n)`) -> `ExecuteUpdateAsync` re-asserting the same
predicate with a new `LeaseToken`/`LeaseOwner`/`LeaseExpiresUtc`/`AttemptCount+1` -> read back
`WHERE LeaseToken == token` (never key on a timestamp - rounding differs per provider). A
single-statement upgrade (`UPDLOCK, READPAST` / `SKIP LOCKED`) is a code comment, not implemented.
`LeasedWorkerBase<TWork>`: adaptive poll 1s backing off to 5s idle, `LeaseOwner =
"{MachineName}:{ProcessId}"`; `OutboxDispatcherService`/`BlobDeleteWorkerService` derive from it, run
on every Scheduler replica (not a TickerQ cron job). `AttemptCount >= 10` sets `DeadLetteredAtUtc`
(row kept - the only surviving copy); admin `POST /api/v1/admin/outbox/{id}/retry` clears it.

**Proof**: N claimers over 1000 rows, no overlap by `LeaseToken`; lease-expiry recovery; rolled-back
save leaves no row.

**Customize**: `OutboxMessage.Payload` has no `HasColumnType` (a comment marks the future
`jsonb`/`nvarchar(max)` split) - don't hardcode a column type here, it reintroduces a provider
branch.

## 7. Consumer inbox (D-029)

**Problem**: at-least-once delivery must not re-project, re-comment, or re-start a workflow for the
same event.

**Shape**: `ConsumerInbox { Consumer(64), MessageId, ProcessedAtUtc }`, PK `(Consumer, MessageId)`.
`IInboxStore.TryClaimAsync(consumer, messageId, ct)` - FlexLabs upsert with `NoUpdate()`: insert
proceeds, no-op means already processed. Shared `IntegrationEventConsumer` base owns
claim -> `ConsumeAsync` -> release-on-exception, around three consumers: projection short-circuits
only (Cosmos upsert already idempotent); ai-review inserts the inbox row and the comment in **one**
`SaveChangesAsync` (duplicate-PK rollback = treated as already-processed); workflow adds
`StartRequest.IdempotencyKey = "{EventType}:{EventId}"` underneath the inbox (FlowEngine's own dedup
key, independent of it).

**Proof**: replaying an identical envelope produces exactly one Cosmos doc / comment / workflow
instance, `createdUtc` unchanged.

**Customize**: a new consumer needs only a `Consumer` name constant and a `ConsumeAsync` override.

## 8. Envelope + three-subscription/queue topology + DLQ

**Problem**: one wire shape regardless of transport, one processing path per concern (projection,
AI review, workflow) instead of one handler `switch`ing on event type.

**Shape**: `IntegrationEventEnvelope(Guid Id, string Type, int Version, Guid TenantId,
DateTimeOffset OccurredAtUtc, string? CorrelationId, JsonElement Payload)` - the full envelope is
always the wire body; broker-native fields are populate-on-publish only, never reconstructed on
receive. Three destinations - `projection` (Created/StatusChanged/Completed), `ai-review` (Created
only), `workflow` (Created only) - map to three Service Bus subscriptions (SQL filter on `EventType`,
topic dup detection, `maxDeliveryCount=5`, dead-letter on expiry/filter error) or three RabbitMQ
queues bound to a topic exchange with a shared DLX. Both readers: malformed -> dead-letter with a
reason, success -> complete/ack, transient -> rethrow/nack-requeue so the platform's own retry-then-
DLQ takes over.

**Proof**: malformed message lands in the dead-letter path with a reason; exhausted retries land in
the DLQ, not an infinite loop.

**Customize**: a fourth consumer needs a subscription/queue binding on `EventType` plus a new
`IntegrationEventConsumer` subclass; envelope/DLQ shape does not change.

## 9. Messaging provider switch + RabbitMQ package usage (D-034)

**Problem**: prove the outbox/consumer design is transport-agnostic without adopting a general
message-bus framework that duplicates the outbox already owned here.

**Shape**: `Messaging:Provider = ServiceBus | RabbitMq` (env `TASKFLOW_MESSAGING_PROVIDER` wins) -
same one-enum/one-resolver/one-branch shape as pattern 1. `IIntegrationEventTransport { bool
CanDispatch; Task SendBatchAsync(destination, messages, ct) }` has `ServiceBusEventTransport`,
`RabbitMqEventTransport`, `NoOpEventTransport` (`CanDispatch=false`, rows accumulate - local runs
proceed with no broker). RabbitMQ is split in two: `src/Packages/EF.Messaging.RabbitMq` is a
**portable, TaskFlow-free** package (connection multiplexer with a publisher-confirm channel pool,
topology declarer, `RabbitMqConsumerHostedService<THandler>` with per-queue prefetch, health check,
OTel metrics), candidate for the EF.* feed (request 23); `Infrastructure.Messaging.RabbitMq` is the
thin adapter (transport, topology constants, handler wrappers) via `ProjectReference` today -
swapping to a `PackageReference` once published is the entire porting step. Hosting: RabbitMq
selected -> Scheduler registers the three consumer hosted services and Aspire disables the matching
Functions triggers (`AzureWebJobs.<name>.Disabled=true`); ServiceBus selected -> nothing RabbitMQ-side
registers.

**Proof**: `EF.Messaging.RabbitMq.Tests` (31: 15 unit against a fake channel, 16 Testcontainers
integration) prove publisher confirms, prefetch bound, malformed-message DLX, requeue-then-DLX, with
zero TaskFlow dependencies.

**Customize**: a third transport needs a new `IIntegrationEventTransport` impl and enum value; outbox
and consumer-side inbox don't change.

## 10. Real scheduler jobs with deterministic ids + retention sweeps

**Problem**: overdue/recurrence/stale-cleanup were log-only stubs that never mutated data or staged
events, running as global admin regardless of tenant.

**Shape**: `ITaskItemSystemRepository` (Trxn context, `IgnoreQueryFilters()` - cross-tenant system
jobs by design): `StreamOverdueAsync`, `MarkOverdueNotifiedAsync`, `StreamDueTemplatesAsync`,
`UpsertOccurrencesAsync`, `AdvanceNextOccurrenceAsync(tenantId, templateId, expectedNext, newNext)`,
`GetStaleBatchAsync`, `StageBlobDeletesAsync`, `DeleteStaleBatchAsync`. Every staged event uses a
deterministic id (`DeterministicGuid.Create(ns, ...parts)`, real UUIDv5/SHA-1) so a re-run over the
same data produces zero new rows: `overdue` keys on `(tenant, task, dueDate)`, `recurrence` keys on
`(tenant, template, occurrenceUtc)`. Occurrences upsert via FlexLabs `UpsertRange(...).On(...)
.NoUpdate()` (D-028); the template pointer only advances when `expectedNext` still matches - a lost
race skips that tick instead of double-advancing. Stale cleanup stages `BlobDeleteWork` before
deleting the task row (blob cleanup happens via pattern 6, not inline). Four retention jobs purge
dead-lettered outbox/blob-delete rows (>7d, `Scheduling:Retention:OutboxDays`), processed inbox rows
(>7d), executed TickerQ occurrences (>24h), and audit rows (>30d, batched by `tenantId|yyyyMMdd`) -
all four windows are config, not hardcoded. TickerQ `NodeIdentifier = "{MachineName}:{ProcessId}"`
(not machine name alone - Aspire runs two Scheduler replicas locally).

**Proof**: each job run twice asserts the second run produces zero new rows and zero new outbox rows
- the strongest available at-least-once-safe proof.

**Customize**: a new job that stages events must mint its deterministic id the same way, or a re-run
duplicates events.

## 11. FusionCache activation with tags/profiles

**Problem**: FusionCache and its Redis backplane were fully registered but never injected - every
call site only ever called `RemoveAsync` against a no-op provider.

**Shape**: `Contracts/Caching/ITaskFlowCache { GetOrSetAsync<T>(key, factory, profile, ct);
RemoveByTagAsync(tag, ct); RemoveAsync(key, ct) }`, one impl (`FusionTaskFlowCache`).
`CacheKey(CacheKind, TenantId, Discriminator?)` renders `"{env}:{schemaVersion}:{tenantId:N}:
{kind}[:{discriminator}]"`. Two profiles only - `Metadata` (L1 5m/L2 30m/fail-safe 2h/0.8 eager
refresh) and `Summary` (L1 5s/L2 15s/fail-safe 1m/500ms soft timeout) - deliberately narrow: caches
immutable snapshots only, never a single mutable entity. Invalidation is tag-based
(`t:{tenant}`, `t:{tenant}:taskitem|category|tag`), every create/update/delete that can affect a
snapshot invalidates the matching tags. `IEntityCacheProvider`/`NoOpEntityCacheProvider` are deleted
outright, not kept as a fallback.

**Proof**: tag invalidation visible from a second cache instance over the Redis backplane (not just
L1); a degraded backplane fails safe (stale-but-served) rather than failing the request.

**Customize**: a third cached shape needs a new `CacheKind` and, if its freshness need differs, a new
profile - don't reuse `Summary`'s short TTLs for something actually immutable.

## 12. Redis rate limiter, fail-open

**Problem**: the prior limiter was in-process fixed-window per tenant - correct on one replica,
silently too generous across a scaled-out deployment.

**Shape**: `RedisRateLimiting`'s `RedisSlidingWindowRateLimiter` implements the standard
`RateLimiter`, so only the partition factories changed. `FailOpenRateLimiter` wraps it: any inner
exception (except `OperationCanceledException`) admits the request via an always-acquired lease and
increments `taskflow.ratelimit.backend_failure` - a limiter-dependency outage must not become an API
outage. Tiers: `RateLimiting:Tiers:{free|standard|premium}` + per-tenant override
`RateLimiting:TenantTiers:{tenantId}`; default `standard` = 100/60s. The export route (pattern 5) has
its own `"Export"` policy so a slow download doesn't starve interactive tenants on the same budget.

**Proof**: shared budget enforced across two API instances (not per-replica); simulated Redis failure
fails open rather than 500s or false 429s.

**Customize**: `taskflow.ratelimit.backend_failure` needs a real alert wired in any deployment -
fail-open is a deliberate availability trade that becomes a silent gap unmonitored.

## 13. Startup provisioning

**Problem**: `AuditLogRepository` created its table on every append; `BlobStorageRepository`
exists-checked its container on every call - correct once, wasteful forever after.

**Shape**: one `IStartupTask`, `EnsureExternalResources.cs` (blob container, audit table, dev-only
Cosmos db/container, RabbitMQ topology when selected), beside the existing `WarmupDependencies`.
Per-call provisioning removed (`CreateContainerIfNotExist = false`).

**Customize**: a new external resource needing "create if missing" belongs in this one task, not a
per-call guard in its repository.

## 14. Token single-flight

**Problem**: the Gateway's service-to-service token acquisition had no de-duplication - N concurrent
callers could trigger N acquisitions.

**Shape**: `TokenService`: `ConcurrentDictionary<string, Lazy<Task<AccessToken>>>` keyed by cluster
id, `Lazy` built with `ExecutionAndPublication` so concurrent callers share one in-flight
acquisition; a token in its refresh window does a compare-and-remove of that exact `Lazy` (guards
against discarding a concurrent refresh) before reacquiring; a faulted `Lazy` is evicted, not cached.
Deliberately process-local (Gateway runs behind a load balancer; per-replica scope is correct).

**Proof**: 50 concurrent callers -> one acquisition; a faulted acquisition is not cached.

**Customize**: this `ConcurrentDictionary<TKey, Lazy<Task<T>>>` + compare-and-remove-on-refresh shape
is the reusable template for any other per-key, de-duplicated, async-memoized, legitimately
process-local resource.

## 15. Generated clients (Refitter, openapi-typescript) and the Uno WASM limitation

**Problem**: three hand-maintained HTTP clients drifted from the API contract independently; wanted
generation from the one committed OpenAPI document without losing each UI's existing HTTP pipeline.

**Shape**: `TaskFlow.Api/openapi-doc/TaskFlow.Api.json` is committed with a drift-guard test.
Blazor: Refitter (`src/UI/TaskFlow.ApiClient/.refitter`, `generateContracts: false` to reuse existing
DTOs) generates `ITaskFlowApiClient.Generated.cs` into a new shared project; one hand-written sibling
(`IAttachmentUploadClient.cs`) covers the multipart upload Refitter can't generate. React:
`openapi-typescript` generates `src/api/types.ts` only (`npm run gen:api`); `client.ts`/hooks stay
hand-written on top. Uno **stays hand-rolled**, not for lack of trying: a bare `ProjectReference` to
`TaskFlow.Application.Models` (pulled in by the generated client and Refit) fails Uno WASM with
`CS0118` - the XAML compiler resolves the unqualified `Application` in `App.xaml.cs` (`class App :
Application`) against the `TaskFlow.Application` namespace segment instead of
`Microsoft.UI.Xaml.Application`, once any referenced assembly exposes that path. Reproduces from the
bare reference alone, independent of Refit; no known workaround short of renaming the
`TaskFlow.Application.*` root, rejected as disproportionate. The hand-rolled client is covered by
`Test.UI` payload tests instead of a generator contract.

**Proof**: OpenAPI drift-guard test; `TaskFlowApiClientPayloadTests` assert the Uno client's hand-
built If-Match/cursor/v7-id shapes.

**Customize**: if a future Uno/Uno.Sdk version resolves `CS0118`, the removal criterion is in the
Uno client's own header comment - retry the generated client there first.

## 16. Bicep profiles (dev/prod, provider params, scale rules)

**Problem**: one Bicep tree supporting two DB providers, two messaging providers, and distinct
dev (cheap, scale-to-zero) vs prod (Hyperscale, HA, always-on) profiles without duplicating the
template.

**Shape**: `main.bicep` takes `databaseProvider` (`SqlServer`|`PostgreSql`) and `messagingProvider`
(`ServiceBus`|`RabbitMq`); each conditionally deploys exactly one matched module pair
(`sql-database.bicep`/`postgres-flexible-server.bicep`, Service Bus module/
`rabbitmq-container-app.bicep`). Three parameter files: `main.bicepparam` (legacy default),
`main.dev.bicepparam` (every host `minReplicas:0`/`concurrentRequests:0`, cheap SKUs),
`main.prod.bicepparam` (Hyperscale `HS_Gen5_2` zone-redundant with 1 HA replica + read-scale, every
host `minReplicas:2`, Gateway/API `concurrentRequests:50`, Scheduler pinned `min:2/max:2` since its
lease-based workers aren't HTTP-scaled). `container-app.bicep`'s `scale.rules` is conditional on
`concurrentRequests > 0` - a scale-to-zero host gets no HTTP rule at all. `postgres-flexible-
server.bicep`: Entra-only auth, pgvector allowlisted, conditional read replica. `rabbitmq-container-
app.bicep`: pinned image, Azure Files mnesia volume, explicitly `minReplicas:1/maxReplicas:1` with a
comment that a second replica would be an independent broker, not a cluster member - don't raise
`maxReplicas` to "fix" that.

**Proof**: Bicep contract tests assert both module pairs declare the right `if()` conditions;
`az bicep build` stays clean.

**Customize**: production RabbitMQ is explicitly out of scope for the container-app module
(documented dev/staging proof in `infra/README.md`) - point real deployments at a managed broker or
an actual cluster.

## 17. Test lanes (dual-style harness, provider matrix, Testcontainers, million-row fixture, load gate)

**Problem**: every pattern above has two independent axes to prove - Service vs CQRS style, SQL
Server vs PostgreSQL - without an N-times blowup in hand-written cases.

**Shape**: `EndpointStyleFixture` runs one test body under both styles via `[DataRow("Service")]`/
`[DataRow("Cqrs")]`, `Assert.Inconclusive` when `TASKFLOW_APPLICATION_STYLE` is pinned by the
environment (env wins over config, so a pinned run genuinely can't exercise the other style - a
correct inconclusive, not a bug). The DB axis is not parameterized per case: `TASKFLOW_TEST_DB_
PROVIDER` selects which Testcontainers image a whole assembly run starts, and CI/local acceptance
runs the same assemblies twice. On Podman/WSL2 machines, every Testcontainers lane additionally
needs a run-scoped `TESTCONTAINERS_HOST_OVERRIDE=<podman machine ip>` (ports don't forward to
`localhost`) - never commit it, it changes on reboot. `MillionRowTaskFixture` (tenant-skewed, ~8%
overdue, ~3% recurring, ~5% stale-cancelled) backs export/summary proofs and `Test.Load`. The 5,000
RPS gate is explicitly deployment-only (`Test.Load/README.md`); Testcontainers + the million-row
fixture is the local proof - a local NBomber run does not satisfy that gate.

**Customize**: a new dual-axis feature extends `EndpointStyleFixture` rather than a second
style-switching mechanism, and asserts identical behavior across both DB providers rather than
testing one "for now."

## 18. Provider switch anatomy (D-035..D-045)

**Problem**: every infrastructure choice needing a Portable-vs-Azure arm needed one copyable recipe
instead of ad hoc per-feature branching, so a reviewer or a new switch author has exactly one shape
to check against, and a mechanical test can prove a switch never ships with a broken default arm.

**Shape**: `Infrastructure.Bootstrapper/Registration/RegisterServices.Messaging.cs` is the original
template (D-034): `enum MessagingProvider { ServiceBus, RabbitMq }`, `MessagingProviderConfigKey`
(`"Messaging:Provider"`)/`MessagingProviderEnvVar` (`"TASKFLOW_MESSAGING_PROVIDER"`) constants,
`ResolveMessagingProvider(IConfiguration)` (env wins over config; when neither is set falls through
to `HostingLaneSelector.Resolve` for the lane default; an unrecognized value throws `ArgumentException`
naming every allowed value), and a `[ProviderSwitch(typeof(IIntegrationEventTransport))] private static
void AddMessagingServices(IServiceCollection, IConfiguration)` dispatcher.
`TaskFlow.Bootstrapper/Registration/ProviderSwitchAttribute.cs` (`[AttributeUsage(AttributeTargets.
Method)]`, one `Type ContractType` property) tags every switch dispatcher so `Test.Architecture` can
discover it by reflection. Five switches carry the attribute and copy the shape exactly:
`RegisterServices.Storage.cs` (`StorageProvider { AzureBlob, S3 }`, `[ProviderSwitch(typeof
(IObjectStorageRepository))]`), `RegisterServices.ReadModel.cs` (`ReadModelProvider { Cosmos, Relational }`,
`ITaskViewRepository`), `RegisterServices.Audit.cs` (`AuditProvider { AzureTable, Relational }`,
`IAuditLogRepository`), `RegisterServices.DataProtection.cs` (`DataProtectionPersistence { AzureBlob,
Redis, None }`, `IDataProtectionProvider` - here the dispatcher is an `IHostApplicationBuilder`+`ILogger`
extension method rather than the `IServiceCollection`+`IConfiguration` shape; the architecture test
recognizes both signatures). Three more switches follow the identical resolver rule (env > config >
lane default > hard default, unknown throws) but are deliberately NOT `[ProviderSwitch]`-tagged: Search
(`Infrastructure.AI/ServiceCollectionExtensions.cs`, `SearchProvider { AzureAiSearch, PgVector, Sql }`)
lives in Infrastructure.AI, which cannot reference `TaskFlow.Bootstrapper` without inverting the
dependency direction, so its default-arm coverage lives in `Test.Unit`'s `SearchProviderSelectorTests`
instead (documented in `ProviderSwitchArchitectureTests`'s own class comment); AiServices
(`RegisterServices.AiChatClient.cs`, `AiProvider { AzureInference, OpenAICompatible, FoundryLocal,
None }`, method `RegisterAiChatClientAsync`) is async and does not match either signature the
reflection sweep recognizes; PoolerMode (`Infrastructure.Data/Provider/TaskFlowDbProvider.cs`,
`PoolerMode { None, Transaction }`, D-045) is a data value folded into `TaskFlowProviderOptions`, not
a service registration, and its resolver (`PoolerModeSelector`) has no environment-variable override
at all - config key `Database:PostgreSql:PoolerMode` only.

**Proof**: `tests/Test.Architecture/ProviderSwitchArchitectureTests.cs` -
`Given_ProviderSwitchDispatchers_When_InvokedUnconfigured_Then_ContractTypeResolves` reflects over
every `[ProviderSwitch]`-tagged method in the Bootstrapper assembly, invokes it against an empty
`IConfiguration`, and asserts the contract type resolves without throwing;
`Given_ApplicationAndDomainAssemblies_When_DependenciesChecked_Then_NoCloudSdkNamespaceUsage` and
`Given_SiblingInfrastructureAssemblies_When_DependenciesChecked_Then_NoAmazonNamespaceUsage` assert the
namespace boundary (only `TaskFlow.Infrastructure.Storage` may reference `Amazon.*`; nothing in
Application/Domain may reference `Azure`/`Microsoft.Azure`/`Amazon`).
`tests/Test.Unit/Hosting/ProviderSwitchSelectorTests.cs` is the selector-table coverage per switch
(env beats config beats lane beats hard default, unknown value throws), with `[DoNotParallelize]` on
the env-var cases since `Environment.SetEnvironmentVariable` is process-global.

**Customize**: a new switch copies `RegisterServices.Messaging.cs`'s five pieces verbatim (enum, two
constants, `Resolve<X>Provider`, `[ProviderSwitch]`-tagged dispatcher) unless its registration cannot
fit one of the two signatures `ProviderSwitchArchitectureTests` recognizes, in which case its
default-arm coverage moves to a same-named `*SelectorTests` class in `Test.Unit` instead, following
the Search precedent.

## 19. Hosting lanes and presets (D-035)

**Problem**: the Portable lane and the Azure lane share almost every switch; without one place that
seeds defaults per lane, adding a lane would mean editing every switch's resolver instead of adding
one map, and a registration site reading the lane directly would give that one feature a second,
inconsistent branch point.

**Shape**: `TASKFLOW_LANE = Azure | Portable` (env wins over `Hosting:Lane` config, default `Azure`,
unknown value throws) is resolved once by `HostingLaneSelector`
(`src/Application/TaskFlow.Application.Contracts/Configuration/HostingLaneSelector.cs`) - placed in
Application.Contracts, the lowest project both Infrastructure.Data (the Database switch) and
Infrastructure.AI (the Search switch) already reference, so neither Infrastructure project takes on a
Bootstrapper dependency for one enum parse. `TaskFlow.Bootstrapper/Registration/HostingLane.cs` holds
`LaneDefaults.Portable`, a static class of `const` fields (`Database = TaskFlowDbProvider.PostgreSql`,
`Messaging = MessagingProvider.RabbitMq`, `Storage = StorageProvider.S3`, `ReadModel =
ReadModelProvider.Relational`, `Audit = AuditProvider.Relational`, `Search = SearchProvider.Sql`,
`AiServices = AiProvider.OpenAICompatible`, `DataProtection = DataProtectionPersistence.Redis`) that
every switch's own `Resolve<X>Provider` reads only when neither its env var nor its config key is set;
Azure-lane values are not listed because they equal each switch's pre-existing hard default (including
the dynamic ones - AI and Search derive from other settings, Data Protection from the blob-URL
presence). `src/Host/Aspire/AppHost/LaneDefaults.cs` restates the identical parsing rule (env wins,
unknown throws) rather than referencing the host-side type - an Aspire AppHost's project references
are resource declarations, not compile-time assembly references - and produces a `LaneSwitches` record
whose `HostEnvironment` dictionary is what every host actually receives (`Hosting__Lane` plus one
`*__Provider`/`*__Persistence` key per switch the lane seeds; `Database__Provider` and
`Messaging__Provider` are written by their own pre-existing call sites and deliberately not repeated
here). No registration site reads `TASKFLOW_LANE` directly outside these two lane-default maps; every
other piece of code calls only its own switch's `Resolve<X>Provider`.

**Proof**: `tests/Test.Aspire/AppHostLaneTopologyTests.cs` - `LaneContract_MatchesTheHostSideSelector`
pins the AppHost's restated env var/config key/enum-member names against the real
`HostingLaneSelector`/`HostingLane`; `PortableLane_ResolvesPortableSwitchesAndHostEnvironment` and
`AzureLane_WritesOnlyTheLaneItselfAndKeepsTodaysDefaults` assert the exact `HostEnvironment` dictionary
each lane produces; `SwitchEnvironmentVariable_BeatsTheLaneDefault`/
`SwitchConfigurationKey_BeatsTheLaneDefaultAndLosesToItsEnvironmentVariable` prove precedence;
`UnknownLane_FailsFastInsteadOfSilentlyRunningTheAzureGraph` proves the fail-fast;
`PortableLane_DropsAzureResourcesAndDeclaresMinio` asserts the AppHost source text at the
container-declaration level - this machine cannot start an Aspire graph under Podman (see pattern 38),
so this test asserts against the resolver output and the AppHost source rather than a running graph.

**Customize**: a third lane adds one more `LaneDefaults` class (host side) and one more arm to the
restated `ParseLane`/`Switch` logic (AppHost side); every switch's resolver already has room for a
third fallback value with no further edits.

## 20. Relational read model + audit sink pattern (D-038/D-039)

**Problem**: the Portable lane has no Cosmos DB or Azure Table Storage, so the read-model projection
and the audit sink each need a relational arm the existing producers and consumers cannot tell apart
from the Cosmos/Table original.

**Shape**: `TaskViewRecord` (`Infrastructure.Data/ReadModel/TaskViewRecord.cs`) is a plain class, NOT
an `ITenantEntity` (tenant passed explicitly on every call, the relational stand-in for a Cosmos
partition key), PK `(TenantId, Id)`, with the free-form part of the projection (`Description`, `Tags`,
via the nested `TaskViewBody` record) serialized into a `Document` string column through the
source-generated `TaskViewBodyJsonContext` - a plain string, not `jsonb`, to stay provider-neutral
(D-030). `AuditLogRecord` (`Infrastructure.Data/Operational/AuditLogRecord.cs`) is likewise plain, PK
`(TenantId, RecordedUtc, Id)`, `TenantId` non-null (a system entry carries
`AuditLogStorageSettings.NullTenantPartitionKey`). The keyset continuation token
(`Infrastructure.Repositories/TaskViewKeysetToken.cs`, still app-local: `EF.Data.Contracts.CursorCodec`
carries a `Guid` tie-break key and this one is the Cosmos-parity string document id) is Base64Url over
`v1|tenantId|lastModifiedUtcTicks|id` (raw UTC ticks, not `"O"`, because the value round-trips into a
`WHERE` clause and PostgreSQL `timestamptz` keeps microseconds while SQL Server keeps 100ns); the
tenant is re-checked against the caller's tenant on decode, and a malformed, truncated, or
foreign-tenant token throws `ArgumentException` (the global handler maps that to 400) rather than
silently restarting the page - the token carries no MAC, since the query is tenant-filtered
server-side regardless. `RelationalTaskViewRepository.PatchCountersAsync` is one
`ExecuteUpdateAsync` with a statement-bodied `SetProperty(e => e.CommentCount, e => e.CommentCount +
delta)` per non-zero counter plus `LastModifiedUtc`, so two concurrent counter-delta events add rather
than clobber each other; zero rows affected is a no-op matching the Cosmos 404 arm. Repository class
names: `RelationalTaskViewRepository` (constructor takes both `TaskFlowDbContextTrxn` for writes/patches
and `TaskFlowDbContextQuery` - the read-replica connection - for gets/pages) and
`RelationalAuditLogRepository` (append is a FlexLabs `Upsert(...).On(...).NoUpdate()` insert-only
upsert on `(TenantId, RecordedUtc, Id)`; purge is `ExecuteDeleteBatchedAsync` keyed on `Id`). Migrations:
`20260908222032_AddRelationalReadModelAndAudit` (`TaskFlow.Infrastructure.Data.Migrations.SqlServer`)
and `20260908222051_AddRelationalReadModelAndAudit`
(`TaskFlow.Infrastructure.Data.Migrations.PostgreSql`), both under `Migrations/TaskFlow/`.

**Proof**: `tests/Test.Integration/RelationalTaskViewRepositoryTests.cs` and
`RelationalAuditLogRepositoryTests.cs` (per the orchestration session log, 50 tests passed on each of
the SqlServer and PostgreSql lanes) - concurrent counter deltas lose nothing, a missing-document patch
or delete is a no-op, paging repeats or skips nothing, retention batches purge correctly. `Test.Endpoints`
runs on `UseInMemoryDatabase`, which supports neither `ExecuteUpdateAsync` nor the FlexLabs upsert, so
relational-arm endpoint coverage lives only in `Test.Integration`.

**Customize**: a consumer adding a new counter to the read model extends
`PatchCountersAsync`'s per-field `if (delta != 0) SetProperty(...)` list and the matching Cosmos-side
field name constant, keeping both arms in the same statement-bodied shape rather than falling back to
a read-modify-write.

## 21. S3 object storage pattern (D-037)

**Problem**: the Portable lane has no Azure Blob Storage, so attachments need an S3-compatible arm
(MinIO locally/on the VPS, any S3-compatible provider in production) behind the unchanged
`EF.Storage.Contracts.IObjectStorageRepository` contract, including presigned download URLs a browser
can actually reach.

**Shape**: this is now the published `EF.Storage.S3` package (package request 25); the app-local
`Infrastructure.Storage/S3/` copy this pattern originally described is deleted. The shape survives
unchanged in the package. `EF.Storage.S3.S3ObjectStorageRepository` takes
two `IAmazonS3` clients - the plain one for upload/download/delete/exists against
`S3StorageSettings.ServiceUrl`, and a keyed one (key `"s3-public"`) used only to sign presigned URLs
against
`S3StorageSettings.PublicServiceUrl` - because SigV4 signs the `Host` header into the signature, a URL
signed against an in-network host like `http://minio:9000` would be unreachable and unfixable by
rewriting the host afterward. `GetPresignedUrlAsync` derives `Protocol` from whether `PublicServiceUrl`
starts with `http://` (MinIO/local without TLS) rather than always defaulting to HTTPS. Bucket = the
existing `containerName` argument (the Azure arm's container concept carries over unchanged); key =
the existing `{tenantId}/{ownerId}/{fileName}` convention (owned by the app's
`AttachmentBlobs` helper) - this repository applies no further transformation.
`EF.Storage.S3.S3StorageSettings`
(`ConfigSectionName = "Storage:S3"`) requires `PublicServiceUrl`, and the app still fails fast eagerly in
`RegisterServices.AddS3StorageServices` (not deferred to `ValidateOnStart`) with an
`InvalidOperationException` naming the missing key, since a missing public endpoint is a configuration
error the moment the `S3` arm is selected, not a surprise on the first download; `ForcePathStyle`
defaults `true` (required by MinIO and most non-AWS S3-compatible servers); `DownloadUrlLifetime`
defaults one hour. Bucket provisioning is the package's `IS3BucketProvisioner`/`S3BucketProvisioner`,
which keeps `Amazon.*` out of Bootstrapper, invoked from `EnsureExternalResources` (pattern 13)
with a null guard exactly like the existing blob-container check, not a separate one-shot container.

**Proof**: `tests/Test.Integration/S3ObjectStorageRepositoryTests.cs` against
`Test.Integration/Infrastructure/MinioContainerFixture.cs` (a copy of the existing
`AzuriteContainerFixture` shape). `tests/Test.Unit/Infrastructure/S3StorageRegistrationTests.cs` proves
the `PublicServiceUrl` fail-fast and the DI registration shape directly (`TaskFlow.Bootstrapper` grants
the test project `InternalsVisibleTo` so it can call `AddS3StorageServices` without going through the
`[ProviderSwitch]` dispatcher).

**Customize**: a fourth S3-compatible provider (a different managed object-storage vendor) is a
configuration change only (`ServiceUrl`/`Region`/credentials), never a new code arm, as long as it
speaks the S3 API; a genuinely different object-storage API needs a new `StorageProvider` enum member
and its own `Add<X>StorageServices` arm beside `AddS3StorageServices`.

## 22. Config + feature flags (D-042)

**Problem**: the ad hoc `AiServices:Use*` kill switches did not generalize, and there was no dynamic,
per-tenant flag mechanism at all - every toggle needed a restart.

**Shape**: `RegisterServices.AddTaskFlowAppConfiguration(IHostApplicationBuilder)`
(`RegisterServices.AppConfiguration.cs`) is a no-op unless `AppConfig:Endpoint` or
`ConnectionStrings:AppConfig` is set, so every host still boots from appsettings/env alone locally -
the `FeatureManagement` section in appsettings is the fallback in that case (every flag on there). When
configured: `ConfigureKeyVault(kv => kv.SetCredential(credential))`, `.Select(KeyFilter.Any,
labelFilter)`, `.ConfigureRefresh(r => r.Register("TaskFlow:Sentinel", refreshAll: true)
.SetRefreshInterval(...))`, `.UseFeatureFlags(...)`, both refresh intervals 30 seconds
(`AppConfigRefreshInterval`). `AddTaskFlowFeatureManagement` (called from the shared infrastructure
registration so Api, Scheduler, and any RabbitMQ/Functions consumer host all resolve
`IVariantFeatureManager` regardless of which one runs the AiReview consumer; Gateway does not reference
Bootstrapper and is intentionally not a caller) registers `AddFeatureManagement()
.WithTargeting<TenantTargetingContextAccessor>()`. `TenantTargetingContextAccessor`
(`Registration/TenantTargetingContextAccessor.cs`) resolves `IRequestContext<string, Guid?>` via
`IServiceProvider.GetService` (not a constructor dependency, since Gateway never registers a request
context) and feeds the tenant id as `TargetingContext.UserId`, falling through to an empty targeting id
rather than a startup failure when there is none. `FeatureGateEndpointFilter`
(`Host/TaskFlow.Api/Filters/FeatureGateEndpointFilter.cs`) answers 404, not 403, when its flag is off -
a disabled surface should look absent, not merely forbidden - and takes an optional
`Func<EndpointFilterInvocationContext, bool> appliesWhen` for a flag that narrows one request shape
rather than gating a whole route (`SemanticSearch` narrows only `mode=Semantic` on `/search/tasks`,
keyword search keeps answering). `RequireFeature(name)`/`RequireFeature(name, appliesWhen)` are the
route-builder extensions. Real `TaskFlowFeatures` constants
(`Application.Contracts/TaskFlowFeatures.cs`): `TaskViews`, `Export`, `SemanticSearch` (all three
endpoint-filter-gated, 404 when off), `AiReview` (consumer-side, checked in `AiTaskReviewer` via
`IVariantFeatureManager.IsEnabledAsync`, skips the review and logs `AiReviewerSkippedByFlag` when off).

**Proof**: `tests/Test.Endpoints/FeatureFlagEndpointTests.cs` -
`Given_ExportFlagOff_When_Export_Then_NotFound`/`..FlagOn_..Then_StreamsSeededTask`,
`Given_TaskViewsFlagOff_When_ListTaskViews_Then_NotFound`/`..FlagOn_..Then_Ok`,
`Given_SemanticSearchFlagOff_When_SemanticSearch_Then_NotFound` paired with
`..FlagOff_When_KeywordSearch_Then_StillAnswers` (proving the narrowing predicate), and
`Given_NoAppConfigEndpoint_When_HostBoots_Then_AppsettingsFeatureFlagsApply` (the local fallback path).
`tests/Test.Architecture/FeatureManagementArchitectureTests.cs` bounds which projects may reference
Microsoft.FeatureManagement.

**Customize**: a new dynamic flag adds one `TaskFlowFeatures` constant plus either a
`RequireFeature(...)` on its route or an `IVariantFeatureManager.IsEnabledAsync` check at its
consumer-side call site; no new plumbing is needed since App Configuration and the local
`FeatureManagement` fallback both already read arbitrary flag names.

## 23. Data Protection persistence switch (D-043)

**Problem**: the Portable lane has no Azure Blob Storage to persist the Data Protection key ring in,
and the prior wiring lived inline in `TaskFlow.Api`'s `Program.cs`, unavailable to the Gateway or
Scheduler.

**Shape**: `DataProtectionPersistence { AzureBlob, Redis, None }`
(`RegisterServices.DataProtection.cs`), config key `DataProtection:Persistence`, env
`TASKFLOW_DATAPROTECTION_PERSISTENCE`. `ResolveDataProtectionPersistence` returns a nullable enum: null
means "derive the legacy default" (`AzureBlob` when `DataProtectionKeysFileUrl` is configured, else
`None`) rather than a fixed value, since an explicit lane default (Portable = `Redis`) must still be
able to win over that derivation only when nothing else is set. `AddTaskFlowDataProtection` (an
`IHostApplicationBuilder` extension, `[ProviderSwitch(typeof(IDataProtectionProvider))]`) was lifted out
of `TaskFlow.Api/Program.cs` together with `CreateAzureCredential`, so Gateway and Scheduler now share
the identical wiring instead of duplicating it. `AzureBlob` requires `DataProtectionKeysFileUrl` (throws
`InvalidOperationException` if absent) and calls `PersistKeysToAzureBlobStorage`. `Redis` requires the
existing `Redis1` connection string and calls `PersistKeysToStackExchangeRedis` with an eagerly-created
`ConnectionMultiplexer.Connect` - a documented shortcut (`RegisterCachingServices` builds its own Redis
connections internally through FusionCache's wrappers and never exposes a shared
`IConnectionMultiplexer`, so there is nothing to reuse today). `None` logs a warning via the
source-generated `logger.DataProtectionPersistenceNone(appName, env)`
(`Registration/LogMessages.cs:42`) - cursor tokens do not survive a restart or reach other replicas.
Key encryption via Azure Key Vault (`DataProtectionEncryptionKeyUrl` -> `ProtectKeysWithAzureKeyVault`)
is independent of the persistence arm and applies under all three.

**Proof**: covered by `tests/Test.Unit/Hosting/ProviderSwitchSelectorTests.cs`'s Data Protection
selector-table cases (env/config/lane precedence, unknown value throws) and by
`Given_ProviderSwitchDispatchers_When_InvokedUnconfigured_Then_ContractTypeResolves` in
`ProviderSwitchArchitectureTests.cs`, which invokes `AddTaskFlowDataProtection` against an empty
configuration and asserts `IDataProtectionProvider` still resolves (the derived-default `None` arm).

**Customize**: a fourth persistence backend adds an enum member, a case arm in the `switch`, and its
own required-setting guard following the `AzureBlob`/`Redis` pattern - `dpBuilder` (from
`services.AddDataProtection()`) is shared across every arm, so only the `PersistKeysTo*` call changes.

## 24. Postgres pooler mode (D-045)

**Problem**: PgBouncer transaction-mode pooling raises the connection ceiling by multiplexing many
idle app connections onto a small server pool, but it also drops session state (no session-level
prepared statements, no `SET`, no `LISTEN`/`NOTIFY`) - the Npgsql connection string must cooperate or
the app sees "prepared statement already exists" errors or a pool that resets on every checkout.

**Shape**: `PoolerMode { None, Transaction }` and `PoolerModeSelector`
(`Infrastructure.Data/Provider/TaskFlowDbProvider.cs`), config key only -
`Database:PostgreSql:PoolerMode` (no environment-variable override, unlike every other switch in this
list; unknown value throws). `TaskFlowProviderOptions.PoolerMode` is folded into the same record that
already carries the connection string and retry settings, resolved by
`PoolerModeSelector.Resolve(configuration)` inside `TaskFlowProviderOptions.FromConfiguration`. When
`PoolerMode.Transaction`, `UseTaskFlowProvider`'s PostgreSql arm calls `AppendTransactionPoolerFlags`,
which builds an `NpgsqlConnectionStringBuilder` with `NoResetOnClose = true` and `MaxAutoPrepare = 0` -
literally "No Reset On Close=true;Max Auto Prepare=0" appended to the connection string. Portable lane:
`deploy/compose/pgbouncer/pgbouncer.ini` runs `edoburu/pgbouncer` under the compose `pooler` profile,
`pool_mode = transaction`, listening on 6432 in front of Postgres's 5432, with
`ignore_startup_parameters = extra_float_digits,options` (Npgsql sends both on connect; PgBouncer must
be told to ignore rather than refuse them in transaction mode). Azure lane:
`infra/modules/postgres-flexible-server.bicep`'s `pgBouncerEnabled` param sets the
`pgbouncer.enabled` server configuration (Flexible Server ships PgBouncer as a server parameter, not a
sidecar; requires GeneralPurpose or MemoryOptimized - the Burstable tier has no `pgbouncer.*` server
parameters and the deployment fails there) and switches the connection port from 5432 to 6432; the
module outputs `poolerMode` (`'Transaction'`/`'None'`) for the app's `Database__PostgreSql__PoolerMode`
setting.

**Proof**: `PoolerModeSelector.Resolve` and `AppendTransactionPoolerFlags` are covered by
`tests/Test.Unit/Hosting/ProviderSwitchSelectorTests.cs`'s pooler-mode cases (unknown value throws;
config-only, no env override).

**Customize**: `UseVector()` is registered unconditionally on the PostgreSql arm regardless of pooler
mode (the model branch in `OnModelCreating` for `TaskItemEmbedding`, pattern 36, is unconditional on
Npgsql too), so enabling the pooler never needs a pgvector-specific adjustment; a PgBouncer session-mode
deployment would need `PoolerMode.None` (no flags appended) since session mode preserves session state.

## 25. Runtime profile props (D-047, G1)

**Problem**: no host set `ServerGarbageCollection`/DATAS/tiered-PGO/invariant-globalization explicitly,
so every deployable host silently ran whatever the SDK's own defaults happened to be, undocumented and
one dropped line away from reverting.

**Shape**: `src/Host/TaskFlow.Host.props` is imported explicitly (`<Import Project="..\TaskFlow.Host.
props" />`) by six host/UI csproj files rather than dropped in as a `Directory.Build.props`, because
`src/Host/` also holds libraries (Bootstrapper, ServiceDefaults) and the AppHost launcher that should
not carry a server runtime profile. It sets `ServerGarbageCollection=true`,
`ConcurrentGarbageCollection=true`, `GarbageCollectionAdaptationMode=1` (DATAS, on by default for
Server GC since .NET 9 but declared explicitly), `TieredPGO=true`, `TieredCompilation=true`,
`InvariantGlobalization=true`, `UseSystemResourceKeys=false`. Per-host overrides declared AFTER the
import so MSBuild's last-assignment-wins semantics apply: `TaskFlow.DatabaseMigrator.csproj` overrides
`ServerGarbageCollection=false` (a run-to-completion job gets no throughput benefit from per-core heaps);
`TaskFlow.Blazor.csproj` and `TaskFlow.Functions.csproj` both override `InvariantGlobalization=false`
(culture-rendering hosts need real ICU) and both run on the `aspnet:10.0-noble-chiseled-extra` base
image (plain chiseled ships no ICU/tzdata and fails at startup with "Couldn't find a valid ICU package"
if paired with `InvariantGlobalization=false`). `PublishReadyToRun=true` plus `-r linux-x64` on BOTH
the restore and publish lines applies only to the Api and Gateway Dockerfiles (`--self-contained false`,
framework-dependent on the chiseled aspnet base). `EnableRequestDelegateGenerator` is on in the Api.
Container memory is left entirely to the runtime's cgroup-aware sizing plus DATAS; no
`DOTNET_GCHeapHardLimit`/`DOTNET_GCHeapHardLimitPercent` is set anywhere in the repo.

**Proof**: `tests/Test.Architecture/HostRuntimeSettingsTests.cs` -
`Given_DeployableHosts_When_ProjectFileRead_Then_ImportsSharedRuntimeProfile` (all six import the
props file by exact relative path), `Given_SharedRuntimeProfile_When_Read_Then_
DeclaresEveryRequiredProperty` (the props file itself still sets every required property to its
committed value), `Given_HostsWithDifferentNeeds_When_ProjectFileRead_Then_OverridesFollowTheImport`
(each override textually appears after the `<Import>`), `Given_CultureRenderingHosts_When_
DockerfileRead_Then_BaseImageCarriesIcu` (Blazor and Functions Dockerfiles pin the `-chiseled-extra`
base), `Given_EdgeHostDockerfiles_When_Read_Then_PublishReadyToRunForLinuxX64` (Api/Gateway Dockerfiles
pass `-r linux-x64` to both restore and publish, exactly twice, plus the BuildKit nuget-credential
secret mount), `Given_HostProjectsAndDockerfiles_When_Read_Then_NoGCHeapHardLimitIsPinned`.

**Customize**: a new deployable host adds the `<Import Project="..\TaskFlow.Host.props" />` line (the
architecture test enforces this the moment the host is added to its `Hosts` array) and any override it
genuinely needs, declared after the import.

## 26. Source-generated JSON contexts (D-048, G1)

**Problem**: every HTTP/cache/broker payload was serialized through STJ's reflection-based resolver,
which builds metadata at runtime instead of compile time and cannot participate in trimming/AOT.

**Shape**: three contexts, split along the same dependency-direction lines the rest of the codebase
already respects. `TaskFlowJsonContext` (`Application.Models/Serialization/TaskFlowJsonContext.cs`)
covers every HTTP/cache/UI DTO, request, and response shape (entity DTOs, read-model DTOs, search
filters, the closed generic `DefaultRequest<T>`/`SearchRequest<TFilter>`/`DefaultResponse<T>`/
`PagedResponse<T>`/`CursorPage<TaskItemDto>` instantiations actually bound by the endpoints - the last
three from `EF.Common.Contracts` - listed
again in `RegisteredClosedGenerics` since a generic type definition has no `JsonTypeInfo` a scan could
discover). `TaskFlowMessagingJsonContext` (`Application.Contracts/Messaging/
TaskFlowMessagingJsonContext.cs`) is separate because `IntegrationEventEnvelope` lives in
Application.Contracts, which Application.Models does not reference the other way; it declares no
`PropertyNamingPolicy` (PascalCase, byte-identical to what reflection produced) and
`PropertyNameCaseInsensitive=true` so a payload from an older/newer build's naming still deserializes
during a rolling deploy - it covers `IntegrationEventEnvelope` plus every registered event payload
record (`TaskItemCreatedEvent`, `TaskItemContentChangedEvent`, `TaskItemStatusChangedEvent`,
`TaskItemCompletedEvent`, `TaskItemOverdueSuspectedEvent`, `TaskItemRescheduledEvent`,
`CommentAddedEvent`, `AttachmentUploadedEvent`). `TaskFlowApiJsonContext`
(`Host/TaskFlow.Api/Serialization/TaskFlowApiJsonContext.cs`; a second, unrelated file of the same name
exists at `UI/TaskFlow.Uno.Core/Client/TaskFlowApiJsonContext.cs` for the Uno client) covers
`ProblemDetails`/`HttpValidationProblemDetails` and lives in the Api host, not Application.Models,
because `ProblemDetails` comes from the ASP.NET Core shared framework. All three are registered in the
same order at `Host/TaskFlow.Api/RegisterApiServices.cs:70-72` -
`TypeInfoResolverChain.Insert(0, TaskFlowJsonContext.Default)`, `Insert(1, TaskFlowApiJsonContext.
Default)`, `Insert(2, TaskFlowMessagingJsonContext.Default)` - with the reflection resolver left behind
them for third-party types. The envelope throw rule: `IntegrationEventEnvelope.From`'s private
`PayloadTypeInfo` calls `TaskFlowMessagingJsonContext.Default.GetTypeInfo(eventType)` and throws
`InvalidOperationException` ("... is not registered on TaskFlowMessagingJsonContext (D-048). Add a
[JsonSerializable] entry for it alongside its Versions entry.") when the concrete event record has no
generated metadata - a build-time omission, not a runtime condition, hence a throw rather than a
reflection fallback.

**Proof**: `tests/Test.Architecture/JsonContextCompletenessTests.cs` asserts every public DTO/request/
response/page type in `TaskFlow.Application.Models` resolves through
`TaskFlowJsonContext.Default.GetTypeInfo`, catching the "added a DTO, forgot the attribute" regression
at build time rather than at first serialization.

**Customize**: a new event record needs both a `[JsonSerializable]` entry on
`TaskFlowMessagingJsonContext` and a `Versions` dictionary entry on `IntegrationEventEnvelope` (the
context's own doc comment calls out that the two lists must match, or a type is either dropped as
unknown or silently falls back to reflection); a new DTO/request/response type needs a
`[JsonSerializable]` entry on `TaskFlowJsonContext`, enforced by `JsonContextCompletenessTests`.

## 27. Hot paths (G1)

**Problem**: the NDJSON export endpoint built a fresh `Utf8JsonWriter`, rented a buffer, and drove an
async state machine once per row via `SerializeAsync`, paying that cost tens of thousands of times per
tenant export; the outbox dispatcher allocated a fresh `byte[]` per row for every broker publish.

**Shape**: `TaskFlowReadEndpoints.Export` (`Host/TaskFlow.Api/Endpoints/TaskFlowReadEndpoints.cs`)
opens one `Utf8JsonWriter` over `httpContext.Response.BodyWriter` for the whole stream and calls
`writer.Reset(body)` between rows instead of constructing a new writer per row; each row serializes
through the source-generated `TaskFlowJsonContext.Default.TaskItemExportDto` metadata (`Utf8JsonWriter`
is synchronous over `IBufferWriter<byte>`, so the only awaits left are real flushes). It flushes on
`writer.BytesPending >= FlushThresholdBytes` OR at the batch boundary (`written % size == 0`), whichever
comes first - the byte ceiling bounds memory on wide rows, the batch boundary keeps a client on narrow
rows seeing progress; `FlushAsync` observes the request `CancellationToken`, so a disconnected client
stops both the write and the database enumeration behind it. `OutboxBodyBuffer`
(`Infrastructure.Data/Messaging/OutboxBodyBuffer.cs`) rents one `ArrayPool<byte>.Shared` buffer sized to
the exact UTF-8 byte count of a whole outbox batch (`Rent(IReadOnlyList<OutboxMessage>)`), and
`Append(payload)` encodes each row's envelope JSON into a slice of it - one rental per dispatcher poll
instead of one `byte[]` per row. It is used by BOTH transports, not RabbitMQ alone:
`RabbitMqEventTransport.cs` and `ServiceBusEventTransport.cs` each wrap their `SendBatchAsync` call in
a `using var bodies = OutboxBodyBuffer.Rent(messages)` scope that outlives the publish (the broker
holds a body until the send completes).

**Proof**: `tests/Test.Benchmarks/ExportSerializationBenchmarks.cs` (BenchmarkDotNet, not part of the
MSTest run - `dotnet run -c Release --project tests\Test.Benchmarks\Test.Benchmarks.csproj -- --filter
*ExportSerializationBenchmarks*`) benchmarks the old per-row `SerializeAsync` against the reused-writer
shape over 1,000 rows. The measured allocation number is recorded in the orchestration session log
(`docs/plans/portable-lane-guidance-refactor.md`, G1 entry): export of 1,000 rows dropped from 192,072 B
to 136 B allocated; it is not hard-coded in the benchmark file itself (BenchmarkDotNet's
`[MemoryDiagnoser]` measures it per run) so a rerun on this machine is the way to reproduce the exact
figure rather than a committed assertion.

**Customize**: a second NDJSON-style export endpoint should reuse the same reused-`Utf8JsonWriter`-over-
`BodyWriter` shape rather than reintroducing per-row `SerializeAsync`; a new broker transport sending
batched payloads should rent from `OutboxBodyBuffer` per `SendBatchAsync` call rather than caching one
buffer per transport instance, since the dispatcher sends destination groups concurrently (pattern 28)
and a shared buffer across those sends would be a data race.

## 28. Bounded concurrency + blocking-call sweep (D-055, G1)

**Problem**: `BlobDeleteWorkerService` deleted a batch's blobs one at a time, so one slow storage round
trip held up every other independent delete behind it; and nothing in the suite would catch a
newly-introduced `.Result`/`.Wait()` thread-pool block, which under load looks like a slow database
rather than a bad line of code.

**Shape**: `BlobDeleteWorkerService.DeleteBatchAsync` (`Host/TaskFlow.Scheduler/Workers/
BlobDeleteWorkerService.cs`) uses EF.Common's `items.ToAsyncEnumerable().ConcurrentPipeAsync(async
item => ..., Math.Max(1, maxConcurrency), ct)` bounded by `BlobDeleteSettings.MaxConcurrency` (config
section `"BlobDelete"`, default 8); an `OperationCanceledException` during shutdown rethrows so the
batch's lease stays intact for the next replica to retry (safe, since a missing blob already counts as
a successful delete), while any other per-item exception is captured into a `ConcurrentQueue` and the
row is released rather than completed. Lease bookkeeping (`ReleaseAsync`/`CompleteAsync` against
`IOperationalWorkRepository`) stays strictly sequential AFTER the concurrent phase, since the scoped
`DbContext` behind it is not thread-safe. `OutboxDispatcherService`
(`Host/TaskFlow.Scheduler/Workers/OutboxDispatcherService.cs`) does NOT use `ConcurrentBatchAsync` for
its per-destination fan-out - it uses plain `Task.WhenAll(groups.Select(async group => ...))`, with a
code comment explaining why: the destinations are a small, fixed set (one logical channel per event
family - projection/ai-review/workflow/embedding), so `Task.WhenAll` is itself the whole limiter and a
configured concurrency ceiling would be a knob with no real range to tune. `NoBlockingCallsTests`
(`tests/Test.Architecture/NoBlockingCallsTests.cs`) is a regex sweep
(`\)\.Result\b|\.Result;|\.Result\)|\.Wait\(|\.GetAwaiter\(\)\.GetResult\(\)`) over every file under
`src/**/*.cs`, skipping comment/doc-comment lines; its allow-list has exactly one entry:
`src/UI/TaskFlow.Uno.Core/Business/Services/MockHttpMessageHandler.cs` (a test double implementing the
synchronous `HttpMessageHandler.Send` override, with no asynchronous caller to await into).

**Proof**: `tests/Test.Architecture/NoBlockingCallsTests.cs` -
`Given_SourceTree_When_Swept_Then_NoBlockingWaitsOutsideAllowList` fails on any new match outside the
allow-list; `Given_TheAllowListedFile_When_Swept_Then_IsStillDetected` asserts the sweep is not vacuous
by requiring the one allow-listed file to still be flagged (guards against a broken regex or a moved
source tree silently reporting the whole repository clean forever).

**Customize**: a new I/O fan-out over an unbounded or large collection uses `ConcurrentPipeAsync`/
`ConcurrentBatchAsync` with a config-bound `MaxConcurrency`, following `BlobDeleteWorkerService`'s
shape; a fan-out over a small, fixed, code-defined set (like the outbox's per-destination sends) can
use plain `Task.WhenAll` instead, provided a comment records why the set size makes a configured bound
pointless. Any new synchronous wait on async work needs an allow-list entry with a reason, or the
sweep fails the build.

## 29. Health probe contract (D-049, G2)

**Problem**: the prior `/healthz` and `/readyz` pair did not distinguish "restart me" from "stop
routing me traffic", so a database blip could trigger a restart storm instead of just draining traffic,
and Blazor Server (stateful SignalR circuits) had no session affinity at the load balancer.

**Shape**: `ServiceDefaults/Extensions.cs`'s `MapDefaultEndpoints` maps three routes, identical on every
host: `/healthz/live` (`Predicate = r => r.Tags.Contains("live")`, only the always-registered `"self"`
check tagged `["live"]` in `AddDefaultHealthChecks` - a liveness failure means restart the process, so
it must never depend on anything a restart cannot fix); `/healthz/ready` (`Predicate = r =>
r.Tags.Contains("ready")`, covering database, outbox, scheduler, and the broker on consumer hosts -
after G2 merged, RabbitMQ health is tagged `ready`); `/healthz` (`Predicate = _ => true`, every
registered check, for humans and Compose healthchecks). `/readyz` was removed entirely. All three are
`.AllowAnonymous()`. The cache is deliberately NOT tagged `ready` - a degraded distributed cache falls
back to L1 rather than failing the request, so it should never take an instance out of rotation.
`infra/modules/container-app.bicep` has three probe path params:
`readinessPath = '/healthz/ready'`, `livenessPath = '/healthz/live'`, `startupPath = '/healthz/live'`,
plus `stickySessions string = 'none'` wired into the container app's `ingress.stickySessions.affinity`.
`main.bicep` sets `stickySessions: 'sticky'` only on the Blazor Server module, with a comment explaining
why: a reconnect landing on another replica loses its SignalR circuit, and every other app in this repo
is stateless across replicas.

**Proof**: `tests/Test.Endpoints/HealthProbeContractTests.cs` -
`Given_LivenessProbe_When_Get_Then_HealthyWithoutDependencies`,
`Given_ReadinessProbe_When_Get_Then_MappedAndAnonymous`,
`Given_RetiredReadyzAlias_When_Get_Then_NotFound` (proves `/readyz` is gone, not merely undocumented).
`tests/Test.Unit/Gateway/GatewayHealthCheckRegistrationTests.cs` covers the Gateway's own aggregate
health wiring.

**Customize**: a new dependency check is tagged `["ready"]` only if losing it should pull the instance
out of load-balancer rotation; a check that should merely be visible on the human `/healthz` page but
never affect routing gets no tag beyond the default (visible only on the aggregate route).

## 30. Edge protection + YARP hardening (D-050, G2)

**Problem**: the Gateway had no active health checking, load-balancing policy, request timeout, or
rate limiting of its own in front of the Api's Redis tenant limiter, so unauthenticated traffic could
exhaust the gateway process before the Api's own protections ever saw it.

**Shape**: `EdgeRateLimitSettings` (`Host/TaskFlow.Gateway/EdgeRateLimitSettings.cs`, section
`RateLimiting:Edge`) - `Enabled=true`, `TokensPerPeriod=200`, `ReplenishmentSeconds=1`, `QueueLimit=0`
(shed immediately with Retry-After rather than hold a connection open at the edge),
`MaxConcurrentRequests=1000`. `RegisterGatewayServices.cs` builds a chained
`PartitionedRateLimiter<HttpContext>` (`PartitionedRateLimiter.CreateChained`) as the ASP.NET Core
`RateLimiterOptions.GlobalLimiter`: a per-client-IP token-bucket limiter first, then a global
concurrency limiter as the backstop against a slow downstream. This is explicitly NOT the Api's tenant
limiter and does not replace it - the edge limiter protects the gateway process from unauthenticated
traffic before it reaches the Api; the Api's Redis-backed tenant limiter (pattern 12) protects tenants
from each other after authentication. Ceiling documented in the settings class: the partitions are
in-process, so the real allowance is this budget times the replica count - swap to the Redis-backed
sliding-window shape already used by `TenantRateLimiterFactory` when replica count makes cross-replica
accounting matter. YARP cluster config (`appsettings.json`): `LoadBalancingPolicy:
"PowerOfTwoChoices"`, `HealthCheck.Active` (`/healthz/ready`, 10s interval, 5s timeout,
`ConsecutiveFailures` policy, threshold 3), `HealthCheck.Passive` (`TransportFailureRate`, 30s
reactivation), `HttpRequest.ActivityTimeout = "00:00:30"`, `Version: "2"`,
`VersionPolicy: "RequestVersionOrLower"` (HTTP/2 to the Api where available).

**Proof**: `tests/Test.Unit/Gateway/GatewayEdgeRateLimitTests.cs` -
`GlobalLimiter_BurstOverTheBucket_ShedsWith429AndRetryAfter`,
`GlobalLimiter_ProbeRoutes_AreNeverShed` (health checks bypass the edge limiter),
`GlobalLimiter_WhenEdgeDisabled_IsNotRegistered`.

**Customize**: raising `MaxConcurrentRequests`/`TokensPerPeriod` is a config change; moving to
cross-replica accounting is the documented ceiling upgrade (swap the partition factories for the
Redis-backed shape) rather than a redesign of the chained-limiter structure.

## 31. GET-only hedging (D-051, G2)

**Problem**: a tail-latency read on the Blazor dashboard stalled the whole rendered page waiting on one
slow attempt, but hedging a write would risk a duplicated side effect on a non-idempotent POST.

**Shape**: `ReadHedgingExtensions.AddReadHedging` (`Host/Aspire/ServiceDefaults/
ReadHedgingExtensions.cs`, section `Resilience:Hedging` - `Enabled` default true, `DelayMs` default
250, `MaxHedgedAttempts` default 1) is applied only to the Blazor read `HttpClient`, added AFTER the
standard resilience handler so the hedged pair sits inside the standard pipeline's retry rather than
multiplying attempts by hedges. Two independent guards keep it off writes, because Polly can trigger a
hedge for two different reasons: `ShouldHandle` narrows the package's own transient-outcome predicate
to `IsRead(context)` (GET only) so an outcome-triggered hedge never fires for a non-GET; `DelayGenerator`
returns `Timeout.InfiniteTimeSpan` for a non-GET so the latency-triggered timer (which consults no
outcome at all) never spawns a second attempt for a slow POST - restricting only `ShouldHandle` would
still leave a slow POST duplicated. Cosmos cross-region hedging is a separate, deployment-only gate:
`RegisterServices.Infrastructure.cs`'s `BuildCosmosClientOptions` returns null unless
`Cosmos:Hedging:Enabled` is true, otherwise sets `AvailabilityStrategy =
CrossRegionHedgingStrategy(threshold, thresholdStep)` from `Cosmos:Hedging:ThresholdMs` (default 500)
and `Cosmos:Hedging:ThresholdStepMs` (default 100) - it only helps a multi-region account with
preferred regions configured and multiplies request units on a slow region, so it stays opt-in.

**Proof**: `tests/Test.Unit/Hosting/ReadHedgingTests.cs` -
`Get_SlowerThanTheHedgingDelay_IssuesOneHedgedAttempt`,
`Post_SlowerThanTheHedgingDelay_IsNeverHedged`, `Get_WhenHedgingDisabled_IssuesNoHedgedAttempt`, against
a slow fake handler. `tests/Test.Unit/Infrastructure/CosmosHedgingOptionsTests.cs` covers the Cosmos
gate.

**Customize**: a new read-only HTTP client that would benefit from hedging calls
`.AddReadHedging(config)` on its `IHttpClientBuilder` after its standard resilience handler; hedging
must never be added to a client that also issues writes without the same GET-only double guard.

## 32. Distributed lock (D-052, G2)

**Problem**: two replicas racing to provision the same external resource or declare the same broker
topology at startup would either double-provision or race on a `CREATE`; leases and conditional updates
already solve this for work-table rows but not for one-time startup work.

**Shape**: `IDistributedLock` (`Application.Contracts/Locking/IDistributedLock.cs`) - one method,
`ValueTask<IAsyncDisposable?> TryAcquireAsync(string key, TimeSpan ttl, CancellationToken ct)`,
non-blocking by design (every caller has something better to do than queue), returning null when
another holder has it. Explicitly scoped to one-time startup tasks (external resource provisioning,
broker topology declaration), not work tables, which already coordinate through leases (pattern 6).
`RedisDistributedLock` (`Infrastructure.Caching/Locking/RedisDistributedLock.cs`): acquire is
`StringSetAsync(key, token, ttl, When.NotExists)` (`SET key token NX PX`) with a random per-acquisition
token; release is a Lua script comparing the token before deleting (`if redis.call('get', KEYS[1]) ==
ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end`) so a holder whose TTL already
expired cannot delete the lock a new holder has since taken. Documented ceiling: single node, no RedLock
quorum - acceptable here because every covered caller is idempotent, so the lock avoids concurrent
conflicting work rather than guaranteeing exactly-once; PostgreSQL `pg_try_advisory_lock` was considered
and rejected as the default because it is provider-specific and the dual-provider rule (D-030) forbids
a PostgreSQL-only code path for something Redis already covers on both providers.
`InProcessDistributedLock` (`Infrastructure.Caching/Locking/InProcessDistributedLock.cs`, a
`ConcurrentDictionary<string, SemaphoreSlim>` with a zero-timeout `WaitAsync`) is the fallback when no
Redis connection is configured; `RegisterCachingServices` chooses between the two based on whether a
Redis connection string resolves. The two real call sites: `EnsureExternalResources`
(`Bootstrapper/StartupTasks/EnsureExternalResources.cs`, lock key `"taskflow:provision"`, pattern 13)
and `TaskFlowRabbitMqTopologyStartup` (`Infrastructure.Messaging.RabbitMq/
TaskFlowRabbitMqTopologyStartup.cs`, lock key `"taskflow:rabbitmq-topology"`) - both poll for the lock
up to a wait budget and, on timeout, proceed WITHOUT the lock rather than fail startup (an undeclared
queue breaks the next consumer that subscribes; a stuck lock holder should not block every replica
forever), logging `TopologyLockTimedOut`/`ProvisioningDeferred` respectively.

**Proof**: `tests/Test.Integration/RedisDistributedLockTests.cs` (Redis Testcontainers) -
`TryAcquireAsync_TwoReplicas_SecondWinsAfterRelease`, `TryAcquireAsync_UnreleasedLock_ExpiresOnItsTtl`,
`DisposeAsync_ByAnExpiredHolder_DoesNotFreeTheNewOwnersLock` (the token-compare release proof).
`tests/Test.Unit/Infrastructure/InProcessDistributedLockTests.cs` covers the in-process fallback.

**Customize**: a new one-time, cross-replica startup task takes an `IDistributedLock` constructor
dependency and follows the poll-with-timeout-then-proceed-anyway shape rather than failing startup on a
lock it could not acquire, unless proceeding without the lock is unsafe for that specific task.

## 33. Broker trace propagation (D-053, G2)

**Problem**: every message started a brand-new root trace at the consumer, so the HTTP request that
produced an outbox event was unreachable from the trace of the consumer that handled it - the opposite
of what distributed tracing across a broker hop is for.

**Shape**: `TaskFlowActivitySources` (`Shared/TaskFlow.Observability/Tracing/
TaskFlowActivitySources.cs`) names two sources registered once in ServiceDefaults:
`TaskFlow.Messaging` (broker publish/consume spans) and `TaskFlow.Scheduler` (scheduled-job and
leased-drain spans, used by `BaseTickerQJob` and `LeasedWorkerBase`). `MessagingTrace`
(`.../Tracing/MessagingTrace.cs`) has two entry points: `StartPublish` starts a Producer-kind activity
and injects the resulting W3C trace context via `Propagators.DefaultTextMapPropagator.Inject` through a
caller-supplied `Action<string, string> setHeader` delegate - a delegate rather than a dictionary
because RabbitMQ headers are `object?`-valued UTF-8 byte arrays and Service Bus application properties
are `object`-valued strings, and neither dictionary type converts to the other. `StartProcess` extracts
the context via `Propagators.DefaultTextMapPropagator.Extract` and starts the Consumer-kind activity
PARENTED to that extracted context (`StartActivity(name, ActivityKind.Consumer, parent.
ActivityContext)`) - NOT an `ActivityLink`, contrary to what a link-based design might suggest; the
code comment is explicit that a parent (not a link) is deliberate, because the outbox hop is one
business operation continuing across a process boundary and the point is a single trace from the HTTP
request through the consumer, whereas a link would leave the consumer as its own root, exactly the
disconnected state this decision fixes. Producer spans are emitted from both
`RabbitMqEventTransport.cs` and `ServiceBusEventTransport.cs`; consumer spans from
`RabbitMqConsumerHandlers.cs` (RabbitMQ) and `TaskFlow.Functions/ServiceBusEnvelopeReader.cs` (Service
Bus, inside the Functions host).

**Proof**: `tests/Test.Unit/Infrastructure/BrokerTracePropagationTests.cs` -
`StartPublish_InjectsTraceparentNamingTheProducerSpan`,
`RabbitMqHandler_WithTraceparentHeader_StartsConsumerSpanParentedToIt`,
`RabbitMqHandler_WithoutTraceparentHeader_StartsItsOwnTrace` (using an `ActivityListener` to assert the
consumer activity's parent id matches the producer's).

**Customize**: a new transport's consumer wrapper calls `MessagingTrace.StartProcess` with a
`getHeader` delegate reading whatever header shape that transport carries, and its producer path calls
`StartPublish` with the matching `setHeader` delegate - both existing transports already demonstrate
the two shapes (byte-array headers vs string properties) a third transport is likely to need.

## 34. Internal gRPC read service (D-054, G3)

**Problem**: proving an internal service-to-service read path over gRPC needed a real second protocol
on the Api without breaking its existing REST surface for public clients or requiring Kestrel to serve
h2c and HTTP/1.1 on the same port (it cannot).

**Shape**: proto at `src/Shared/TaskFlow.Contracts.Grpc/Protos/taskflow_read.proto`
(`TaskFlowReadGrpcMapper` in the same project maps between the proto messages and the existing DTOs).
`TaskFlowReadGrpcService` (`Host/TaskFlow.Api/Grpc/TaskFlowReadGrpcService.cs`) is
`RequireAuthorization`-gated and delegates to the SAME `ITaskFlowReadService`/repositories the REST
summary/metadata endpoints use (pattern 5), with per-status `RpcException` mapping. The Api's
`appsettings.json` declares two Kestrel endpoints: `Kestrel:Endpoints:Http:Url = "http://+:8080"` and
`Kestrel:Endpoints:Grpc:Url = "http://+:8081"` with `Protocols: "Http2"` - a dedicated cleartext HTTP/2
port, because Kestrel cannot serve h2c and HTTP/1.1 on one port. The Api Dockerfile drops
`ASPNETCORE_URLS` (which would otherwise override the configured Kestrel endpoints) and exposes 8081.
Blazor consumes the service directly (`AddGrpcClient<TaskFlowRead.TaskFlowReadClient>`, the one
in-cluster service-to-service hop that bypasses the Gateway) through `ClientReadSettings`
(`UI/TaskFlow.Blazor/Services/ClientReadSettings.cs`), a plain `record ClientReadSettings(bool
UseGrpcReads)` resolved once at startup from `Clients:UseGrpcReads` - defaulting to on when a gRPC
address is available and off otherwise, so the Portable lane (or any deployment not publishing the
second port) keeps working with no configuration at all. It is a concrete record injected at call
sites, deliberately not an `IDashboardReads`-style abstraction, because the point of the proof is that
the two transports answer the same question and an interface would hide exactly that.

**Proof**: `tests/Test.Endpoints/TaskFlowReadGrpcTests.cs` - parameterized `[DataRow("Service")]`/
`[DataRow("Cqrs")]` parity tests (`Given_SeededTasks_When_SummaryReadOverGrpc_Then_
ItMatchesTheRestSummary`, `..MetadataReadOverGrpc_Then_ItMatchesTheRestMetadata`,
`..ReadByIdOverGrpc_Then_ItMatchesTheRestTask`) run an in-memory `GrpcChannel` over
`WebApplicationFactory` and assert byte-for-byte parity with the REST response, plus
`Given_UnknownId_When_ReadByIdOverGrpc_Then_ItIsNotFound`/
`Given_MalformedId_When_ReadByIdOverGrpc_Then_ItIsInvalidArgument` for the RPC status mapping.
`tests/Test.Architecture/GrpcArchitectureTests.cs` asserts the gRPC service lives only in the Api host
and reaches data only through the shared read service, never a repository directly.

**Customize**: a new internal read added to this gRPC surface is added to the same proto and the same
`TaskFlowReadGrpcService`, delegating to `ITaskFlowReadService` rather than a repository, so REST and
gRPC keep answering identically without a second implementation to drift.

## 35. MessagePack L2 (D-048/D-056, G3)

**Problem**: FusionCache's L2 (Redis) entries were JSON-only; proving a binary cache payload needed a
serializer switch, and a pre-existing JSON option (`ReferenceHandler.Preserve`) turned out to silently
break every L2 read of a cached summary DTO.

**Shape**: `CacheSettings.Serializer` (`Infrastructure.Caching/CacheSettings.cs`), enum
`CacheSerializer { Json = 0, MessagePack = 1 }`, per named cache. `MessagePack` uses Neuecc
MessagePack (`ZiggyCreatures.FusionCache.Serialization.NeueccMessagePack`) with a contractless resolver
(`ContractlessStandardResolver` plus LZ4 block-array compression) - no DTO attributes needed, unlike a
contract-based MessagePack setup. `CacheSettings.SchemaVersion` (folded into every cache key) is bumped
`1 -> 2` in the same change: the D-056 story, recorded as its own row in
`.scaffold/DESIGN-DECISIONS.md`, is that the JSON cache serializer previously set
`ReferenceHandler.Preserve`, which writes an `$id`/`$values` wrapper STJ needs a settable shape to
deserialize back through - but TaskFlow's DTOs are positional records, so every L2 read of a cached
summary silently missed and fell through to the database, defeating the cache without ever surfacing an
error. The fix removed `ReferenceHandler.Preserve` from the JSON serializer options entirely (not kept
alongside a DTO shape change, which would have rippled into every STJ-serialized contract) and bumped
`SchemaVersion` so every pre-existing L2 entry is treated as a miss rather than a deserialization
failure - no eviction sweep, no version-mismatch handling code, since the version is part of the key
itself. The same schema-versioned-key rule applies to the MessagePack arm: no cyclic-reference metadata,
schema-versioned keys, so a serializer switch on a live cache also needs a `SchemaVersion` bump (called
out in the property's own doc comment) since changing format invalidates entries already stored under
the current version.

**Proof**: `tests/Test.Integration/MessagePackCacheTests.cs` -
`Given_MessagePackSerializer_When_MetadataCrossesReplicas_Then_EveryFieldSurvives` (Redis
Testcontainers, L2 round trip). The D-056 entry itself in `.scaffold/DESIGN-DECISIONS.md` records the
rejected alternatives (keeping `Preserve` and switching DTOs off positional records; a data
migration/purge of existing keys).

**Customize**: switching a named cache's `Serializer` on a live deployment must bump `SchemaVersion` in
the same change, per the property's own doc comment - the schema-version key prefix is what makes the
switch safe without a manual cache flush or a mixed-format read.

## 36. Pgvector semantic search + content-changed event + conditional topology (D-040, P7)

**Problem**: semantic search needed a vector store and an embedding-maintenance pipeline that costs a
model call only when the embeddable text actually changes, exists at all only on the one provider that
can host it, and adds no broker topology on any deployment that does not select it.

**Shape**: `TaskItemEmbedding` (`Infrastructure.Data/ReadModel/TaskItemEmbedding.cs`) - `TenantId`,
`TaskItemId`, `Vector Embedding` (Pgvector's type), `Dimensions`, `ModelId`, `UpdatedUtc`; `const int
DefaultDimensions = 1536` is a constant, not a bound setting, because changing it changes the column
type, and `Search:PgVector:Dimensions` is validated against it at startup rather than allowed to drift
from the deployed migration. It has NO `DbSet` (a `DbSet` would put the type into the SQL Server model
too through EF's set convention) and is mapped in `TaskFlowDbContextBase.OnModelCreating` only when the
active provider is Npgsql - the one new forced branch under D-030, matching the unconditional
`npgsql.UseVector()` call in `TaskFlowDbProvider`'s PostgreSql arm. `TaskItemContentChangedEvent` is
raised by `TaskItem.Update` (`Domain.Model/TaskItem/TaskItem.cs:167`) ONLY when the incoming `title` or
`description` differs from the current value by ordinal string comparison AND the update validates
successfully - a priority-only, status-only, or rejected edit never raises it, so a status or
completion change never pays for a re-embed. `TaskEmbeddingConsumer`
(`Application.MessageHandlers/Consumers/IntegrationEventConsumers.cs`, `ConsumerName = "embedding"`,
inbox-guarded via the shared `IntegrationEventConsumer` base) handles exactly
`TaskItemCreatedEvent`/`TaskItemContentChangedEvent` - the two events that actually touch embeddable
text - and is registered (`RegisterServices.VectorSearch.cs`) only when `Search:Provider` resolves to
`PgVector`. Conditional topology exists only on that arm too: RabbitMQ queue constant `EmbeddingQueue =
"taskflow." + TaskEmbeddingConsumer.Name` = `"taskflow.embedding"`
(`Infrastructure.Messaging.RabbitMq/TaskFlowRabbitMqTopology.cs`); Service Bus subscription `"embedding"`
declared conditionally (`if (searchProvider == 'PgVector')`) in `infra/modules/service-bus.bicep`, with
a matching conditional subscription rule. Fail-fast: `AddVectorSearchServices`
(`RegisterServices.VectorSearch.cs`) checks the database provider only after confirming
`Search:Provider == PgVector`, and throws `InvalidOperationException` naming both config keys when the
resolved database provider is not `PostgreSql` ("...requires Database:Provider=PostgreSql... A SQL
Server 2025 VECTOR arm is the intended future alternative and does not exist yet"). This prerequisite
check deliberately lives in the Bootstrapper (which composes both switches) rather than in
Infrastructure.AI, so an AI adapter is not made to depend on the data layer.

**Proof**: `tests/Test.Integration/PgVectorSearchTests.cs` (Postgres-only, `TestSetup` asserts SQL
container availability) - `SqlServerLane_DoesNotMapTheEmbeddingEntity`, `PostgreSqlLane_
MigrationCreatesTheVectorExtensionAndHnswIndex`, `PostgreSqlLane_SemanticSearchRanksTheNearerTaskFirst_
AndUpsertIsReplayable`, `PostgreSqlLane_DeleteRemovesTheRow_AndIsANoOpWhenAbsent`. Per the session log,
this lane ran 61 passed/1 skipped against PostgreSQL and 59 passed/3 skipped against SQL Server (the
fail-fast and no-mapping cases).

**Customize**: a future embedding-worthy field addition (e.g. a tag-derived summary) extends
`TaskItem.Update`'s `contentChanged` comparison to include it, so the same event/consumer/topology
keeps working without a new event type; a change to `DefaultDimensions` needs a new migration on the
PostgreSql assembly only, since the vector column's dimension is baked into the deployed schema, not
read from configuration at runtime.

## 37. Compose lane assets + VPS deploy workflow (P6)

**Problem**: the Portable lane's compute topology (Docker Compose + Caddy on one VPS) needed hand-
written deployment assets and a manual release/rollback workflow, deliberately without adopting a new
`Aspire.Hosting.Docker` dependency or generated IaC (D-036).

**Shape**: `deploy/compose/` holds `docker-compose.yml` (base services: `caddy`, `migrator`, `api`,
`gateway`, `scheduler`, `blazor`, `redis`, `otel-lgtm`, plus `pgbouncer` gated behind the `pooler`
compose profile), `docker-compose.override.local.yml` (adds `postgres` (`pgvector/pgvector:pg17`),
`rabbitmq`, `minio` - all gated behind the `local` profile, for a fully local run without external
managed services), `Caddyfile`/`Caddyfile.local` (TLS via ACME in production, active health checks
against `/healthz/ready` on gateway and blazor - the chiseled app images have no shell, so there is no
compose-level `healthcheck:` on them, only Caddy's own active probing), `.env.example` (names only,
test-enforced, covering the lane, every `TASKFLOW_*_PROVIDER`, `ConnectionStrings__*`, `Storage__S3__*`,
`AppConfig__Endpoint`, Azure identity vars, `DataProtectionEncryptionKeyUrl`, OTLP endpoint, Caddy
domain/ACME email), `images.env.example` (digest-pin placeholders), `pgbouncer/pgbouncer.ini` +
`userlist.txt.example` (transaction-mode PgBouncer, see pattern 24), `README.md` runbook. `.gitignore`
covers `.env`, `images.env`, and `userlist.txt`. `.github/workflows/build-images.yml` is a reusable
workflow (digest outputs) consumed by both the existing `deploy.yml` (Azure lane) and the new
`deploy-vps.yml`. `deploy-vps.yml` is `workflow_dispatch`-only with a `choice` input
(`operation: deploy | rollback`) and a required `commit_sha` for deploy; it verifies the target SHA has
a successful `build-and-test` check and no failed required checks before building, ships the compose
assets plus digest-pinned `images.env` over plain ssh (pinned `known_hosts`, no third-party ssh action),
runs `docker compose pull && up -d --wait --remove-orphans`, smokes `/healthz/ready` plus a create/read/
delete round trip through Caddy, and records a release manifest artifact (`taskflow-release-manifest-
vps`) that rollback reads back to restore the previous digest-pinned images without rebuilding or
re-migrating. `ci.yml` runs `docker compose ... config -q` (both compose files) on every run, and gates
a full `compose-smoke` job (builds all five app images from source, brings up the `local`-profile stack
on `ubuntu-latest`, and asserts `/healthz/ready` plus one CRUD round trip through Caddy) behind the
`includeComposeSmoke` dispatch input, since it is the single most expensive job in the file and does not
belong on every push.

**Proof**: `ci.yml`'s `compose-smoke` job is the first real (non-source-level) execution of the compose
path on any machine, per the session log - nothing in the compose/VPS path has executed on this dev
machine. `infra/scripts/Test-ReleaseManifest.ps1 -ImagesOnly` validates the release-manifest shape both
`deploy-vps.yml`'s record/rollback steps produce and consume.

**Customize**: a new app service added to the Portable lane needs an entry in `docker-compose.yml` (or
the `.override.local.yml` file if it is a local-only infrastructure dependency), a health-check wired
through Caddy if it serves traffic, and its image added to both `build-images.yml`'s outputs and
`deploy-vps.yml`'s digest-pin verification list.

## 38. Portable test matrix

**Problem**: the Portable lane's container topology (which resources get declared, which env vars every
host receives) needed a proof that does not depend on this dev machine's ability to start an actual
Aspire/DCP graph, which it cannot.

**Shape**: `tests/Test.Aspire/AppHostLaneTopologyTests.cs` (`[TestCategory("Aspire")]`) is written at
two levels specifically so it can run without DCP: it asserts `LaneDefaults.Resolve` (a pure function)
directly for the exact switch values and `HostEnvironment` dictionary each lane produces, and it asserts
the topology decisions those switches feed (which containers get declared) against the AppHost's own
source text (`ReadAppHostSource`, the same technique `AppHostMigratorTopologyTests` already uses) rather
than a running graph. `PortableLane_DropsAzureResourcesAndDeclaresMinio` confirms: Postgres and RabbitMQ
are declared unconditionally (`AddPostgres("postgres")`, `AddRabbitMQ("rabbitmq")`); MinIO is declared
only via source-text assertions on `AddContainer("minio", "minio/minio")`,
`WithHttpEndpoint(targetPort: 9000, name: "s3")`/`WithHttpEndpoint(targetPort: 9001, name: "console")`,
dev-credential parameters, and a persistent volume; every Azure-only resource sits behind an `if
(portableLane)`/`if (!isTesting && !portableLane)`-shaped guard, and `AddAzureStorage`/
`AddAzureServiceBus` never appear at statement level (only inside an else-arm). `PortableLane_
ResolvesPortableSwitchesAndHostEnvironment` pins the exact expected values (`Database=PostgreSql`,
`Messaging=RabbitMq`, `Storage=S3`, `ReadModel=Relational`, `Audit=Relational`, `Search=Sql` - the code
comment notes the Search default stays `Sql` in BOTH lanes, since the PgVector arm needs an embedding
endpoint and is opt-in through `Search:Provider` rather than lane-seeded, `AiServices=OpenAICompatible`,
`DataProtection=Redis`). Per the orchestration session log, this specific test class - 12 non-DCP tests,
7 of them new for the lane work - passed locally, because it never starts an Aspire graph; that is
distinct from the rest of the `Test.Aspire` category and from `Test.PlaywrightUI`, both of which need an
actual running mesh. For those: this is a Podman-backed Windows/WSL2 dev machine, and Aspire/DCP binds
published container ports to `127.0.0.1` inside the WSL VM, unreachable from the Windows host,
independent of the Testcontainers host-override mechanism used by the container-backed test lanes -
`Test.Aspire` (beyond the source-level lane topology tests) and the full-stack `Test.PlaywrightUI` lane
cannot run here until either Docker Desktop replaces Podman or the podman machine networking is
reconfigured for port forwarding; CI is the proof for those lanes, per `AGENTS.md`'s environment-facts
section and `.scaffold/REFERENCE-STATUS.md`.

**Proof**: `tests/Test.Aspire/AppHostLaneTopologyTests.cs` itself, run locally per the session log
(12 passed) despite carrying the `Aspire` category, because its assertions are all source-text/
pure-function level; `tests/Test.Aspire/AppHostMigratorTopologyTests.cs` is the pre-existing precedent
for the same source-level-assertion technique.

**Customize**: a new lane-conditional resource in the AppHost needs a matching source-text assertion in
`AppHostLaneTopologyTests` (guard shape, container declaration, and any `HostEnvironment` keys it adds)
rather than relying on a DCP-started graph to prove the topology, since that graph cannot run on every
contributor's machine.

## Instruction-set gaps observed

Mirrors `.scaffold/INSTRUCTION-GAPS.md` (source repo owns the fix):

- EF.Data hard-couples `Microsoft.EntityFrameworkCore.SqlServer` and Always-Encrypted-only SqlClient
  into a package meant to be provider-neutral (`ef-package-requests.md` requests 3-4).
- No keyset/cursor paging primitive existed in the EF.* packages; every option assumed offset
  semantics.
- The only documented column-encryption pattern was SQL Server Always Encrypted; no provider-neutral
  app-layer primitive existed.
- No PostgreSQL Testcontainers fixture existed in `EF.IntegrationTesting`, only `MsSqlContainerFixture`.
- `resource-implementation.yaml` declared `outboxEnabled: true` with nothing scaffolded to back it -
  the shape existed in the artifact before any code did.
- EF.Storage guidance encouraged per-request `CreateContainerIfNotExist`, correct once and wasteful
  forever after; recommend a startup provisioning task instead (pattern 13).
- On a Podman-backed Windows/WSL2 dev machine, Aspire/DCP binds published container ports to
  `127.0.0.1` inside the WSL VM, unreachable from Windows, independent of the Testcontainers host
  override. No existing instruction addressed this; it blocks the Aspire mesh and full-stack
  Playwright lanes on this class of machine entirely.

## Package dependencies

- `docs/plans/ef-package-requests.md` - full EF.* package change request list (32 items); 9 of 11
  REQUIRED requests already have an app-local fallback marked `// fallback:` at the call site, so
  this repo is not blocked, but the fallback is the natural first replacement once each ships.
- `docs/plans/ef-messaging-rabbitmq-package-spec.md` - the exact public-API spec
  `src/Packages/EF.Messaging.RabbitMq` implements today as a portable, dependency-free project;
  porting it to the real package feed is a move-and-republish, not a rewrite.
