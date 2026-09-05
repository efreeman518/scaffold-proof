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

**Shape**: `TaskFlowEntityBase<TId>` implements `IVersionedEntity { long Version; DateTimeOffset
CreatedAtUtc, ModifiedAtUtc; }` (private setters). `VersionTimestampInterceptor`
(`Infrastructure.Data/Interceptors/`) stamps both timestamps and increments `Version` from its
pre-save original value for every Added/Modified root. Aggregate-level ETag (D-031): only the root's
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

**Shape** (`Application.Models/Paging/`): `CursorSearchRequest<TFilter,TSortMode>{ Filter, SortMode,
PageSize<=100 default 50, Cursor? }`; `TaskItemSortMode { IdAsc, DueDateAsc, DueDateDesc,
ModifiedDesc, StatusThenId }`; `CursorPage<T>{ Data, NextCursor, HasMore }` - no Total, no page
number. `Contracts/Paging/ICursorProtector` + `CursorToken(SortMode, TenantId, SortKey, LastId)`;
`DataProtectionCursorProtector` wraps `IDataProtector` (purpose `"TaskFlow.Cursor.v1"`) - tamper,
cross-tenant, sort-mode mismatch all fail closed to 400. `TaskItemRepositoryQuery.ApplyKeyset`: one
switch per sort mode building the `ORDER BY`/`WHERE` shape, each arm commented with the index it must
hit; null `DueDate` sorts last; `Take(PageSize + 1)` computes `HasMore` without a second query.

**Proof**: page-through with no dup/gap, `HasMore=false` at the end, tamper/cross-tenant/sort-mode
mismatch -> 400, one case per sort mode.

**Customize**: production needs the persisted Data Protection key ring (comment in `Program.cs`) or
cursors break across restarts/replicas; documented alternative is an HMAC over
`Paging:CursorKey`.

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

- `docs/plans/ef-package-requests.md` - full EF.* package change request list (24 items); 9 of 11
  REQUIRED requests already have an app-local fallback marked `// fallback:` at the call site, so
  this repo is not blocked, but the fallback is the natural first replacement once each ships.
- `docs/plans/ef-messaging-rabbitmq-package-spec.md` - the exact public-API spec
  `src/Packages/EF.Messaging.RabbitMq` implements today as a portable, dependency-free project;
  porting it to the real package feed is a move-and-republish, not a rewrite.
