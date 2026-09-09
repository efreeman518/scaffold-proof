# TaskFlow - Scale-Guidance Alignment

This document restates the guidance-alignment matrix from `docs/plans/portable-lane-guidance-plan.md`
Part B.1 and verifies each of its 26 rows against the merged worktree, not against the plan's own
claims. Every "Evidence" cell was produced by opening the named file in this tree; no path or line
number below was carried over from the plan or the orchestration handoff without being re-checked.
Verified against this worktree (`scaffold-proof-wt/pl-g4`, branch `feature/pl-g4-docs`), base commit
`d4d42ee` ("docs: handoff - P7 merged, all code slices done") plus this slice's own
`0c5e250` ("refactor(g4): convert remaining raw Log calls to LoggerMessage, enforce CA1848 (D-053)"),
per `git log --oneline -2` at verification time. All slices P1-P7 and G1-G3 are merged into this tree;
G4 (this doc plus the remaining docs sweep) is in progress - the LoggerMessage/CA1848 sweep referenced
in item 24 below is `0c5e250`, already committed on this branch.

## Alignment matrix

| # | Guidance item | Status after (verified) | Evidence (file:line) | Notes |
|---|---|---|---|---|
| 1 | Stateless scale-out | Done | `src/Host/TaskFlow.Bootstrapper/Registration/RegisterServices.DataProtection.cs:11-43` (Redis persistence arm, D-043); `infra/modules/container-app.bicep:48,77-78` (`stickySessions` param, Blazor only) | Data Protection ring can now persist to Redis so cursor tokens survive a restart/replica change instead of only Azure Blob or the ephemeral ring; Blazor Server keeps sticky sessions for SignalR circuits, other hosts stay unaffected. |
| 2 | EDA via brokers | Done, deviation recorded | `.scaffold/DESIGN-DECISIONS.md:172` (D-046); transport arms at `src/Infrastructure/TaskFlow.Infrastructure.Storage/ServiceBusEventTransport.cs`, `src/Infrastructure/TaskFlow.Infrastructure.Messaging.RabbitMq/RabbitMqEventTransport.cs` | Pre-existing dual-broker transport (D-034) kept; D-046 records the decision not to adopt MassTransit/SlimMessageBus, with the "why" (would duplicate the existing outbox/consumer host) in the same row. |
| 3 | Transactional outbox | Done (pre-existing) | `src/Infrastructure/TaskFlow.Infrastructure.Data/Operational/OperationalWorkBase.cs:24-33` (`OutboxMessage`); `src/Host/TaskFlow.Scheduler/Workers/OutboxDispatcherService.cs` (claim/dispatch/settle) | Unaffected by this refactor except for the D-055 fan-out change to the dispatcher (see item 6). |
| 4 | CQRS | Done | Pre-existing `EF.CQRS.Abstractions` application style (used by `src/Host/TaskFlow.Api/Grpc/TaskFlowReadGrpcService.cs:2,7`); relational read-model arm added by P3: `src/Infrastructure/TaskFlow.Infrastructure.Repositories/RelationalTaskViewRepository.cs` | Read side now has two provider arms (Cosmos, Relational) behind the same `ITaskViewRepository`/`ITaskViewRepository` contract; write/read context split predates this refactor (D-027). |
| 5 | API gateway / edge | Done | `src/Host/TaskFlow.Gateway/appsettings.json:47-88` (`ReverseProxy.Clusters.api-cluster`: `PowerOfTwoChoices`, `HealthCheck.Active`/`Passive`, `HttpRequest.ActivityTimeout`/`Version 2`) | Active health probes `/healthz/ready`, passive transport-failure health, load-balancing policy, and an HTTP/2 activity timeout are all new (G2); previously the cluster had none of these. |
| 6 | Non-blocking, no async-in-loop | Done | `src/Host/TaskFlow.Scheduler/Workers/BlobDeleteWorkerService.cs:67-85` (`ConcurrentPipeAsync`, D-055); `src/Host/TaskFlow.Scheduler/Workers/OutboxDispatcherService.cs:72-94` (`Task.WhenAll` per destination group) | Blob deletes now run at a bounded concurrency (`BlobDelete:MaxConcurrency`, default 8) instead of one at a time; outbox sends fan out per destination. Settlement against the scoped DbContext deliberately stays sequential (documented in the same file) since `DbContext` is not thread-safe. |
| 7 | Server GC + DATAS | Done | `src/Host/TaskFlow.Host.props:23-43` (`ServerGarbageCollection`, `ConcurrentGarbageCollection`, `GarbageCollectionAdaptationMode=1`, `TieredPGO`) | Imported explicitly by the six host csproj files (Api, Gateway, Scheduler, Blazor, Functions, DatabaseMigrator); DatabaseMigrator overrides `ServerGarbageCollection` to `false` for its short-lived-job profile (per the session log; not independently re-verified per-project in this pass beyond the import test below). `HostRuntimeSettingsTests` at `tests/Test.Architecture/HostRuntimeSettingsTests.cs` exists to enforce every host imports the file. |
| 8 | Zero-allocation hot paths | Done (hot paths only) | `src/Host/TaskFlow.Api/Endpoints/TaskFlowReadEndpoints.cs:91-97` (one reused `Utf8JsonWriter` over `Response.BodyWriter`); benchmark `tests/Test.Benchmarks/ExportSerializationBenchmarks.cs` | Benchmark compares the old per-row `SerializeAsync` (reflection) against the reused-writer/source-generated path; the session log records the measured result (1,000 rows: 192,072 B -> 136 B allocated) but that number is a benchmark run output, not something re-executed in this verification pass - the benchmark code and the endpoint code it models are both confirmed present. Scope is the export writer only, not every allocation in the codebase. |
| 9 | STJ source generators / AOT | Done / AOT deferred with record | `src/Application/TaskFlow.Application.Models/Serialization/TaskFlowJsonContext.cs` (`[JsonSourceGenerationOptions]` + `[JsonSerializable]` list); wired at `src/Host/TaskFlow.Api/RegisterApiServices.cs:70-72` (`TypeInfoResolverChain.Insert(0/1/2, ...)` for `TaskFlowJsonContext`, `TaskFlowApiJsonContext`, `TaskFlowMessagingJsonContext`); `EnableRequestDelegateGenerator` at `src/Host/TaskFlow.Api/TaskFlow.Api.csproj:7`; AOT record at `.scaffold/DESIGN-DECISIONS.md:173` (D-047) | AOT deferred concretely because: EF Core 10 Native AOT support is experimental (precompiled queries only), and both TickerQ and FlowEngine are reflection-based, so the app as a whole cannot trim/AOT-compile cleanly. Only `src/Packages/EF.Messaging.RabbitMq` (no TaskFlow dependency, no reflection of its own) got `IsAotCompatible=true` (`EF.Messaging.RabbitMq.csproj:15`). `JsonContextCompletenessTests` at `tests/Test.Architecture/JsonContextCompletenessTests.cs` enforces every DTO/Request/Response/Page type resolves through the generated context. |
| 10 | Hybrid two-level cache | Done | Pre-existing FusionCache L1/L2 (`src/Infrastructure/TaskFlow.Infrastructure.Caching/RegisterCachingServices.cs`, `FusionTaskFlowCache.cs`); MessagePack L2 option added by G3: `src/Infrastructure/TaskFlow.Infrastructure.Caching/CacheSettings.cs:44-64` (`CacheSerializer` enum, `Json`/`MessagePack`) | `SchemaVersion` bumped 1->2 in the same change (`CacheSettings.cs:38-42`, D-056) because the JSON options previously used `ReferenceHandler.Preserve`, which cannot deserialize positional records - every L2 read of a cached summary DTO was silently missing before the fix. |
| 11 | On-the-fly calculation | Done (pre-existing) | `src/Application/TaskFlow.Application.Models/Reads/TaskItemSummaryDto.cs:8-18` (tenant-wide counts "computed in one database round trip"); `src/Application/TaskFlow.Application.Services/TaskFlowReadService.cs:32` (`GetTaskItemSummaryAsync`) | Unaffected by this refactor; cited for completeness since the item was already "Done" per the plan. |
| 12 | IAsyncEnumerable pipelines | Done (pre-existing) | `src/Infrastructure/TaskFlow.Infrastructure.Repositories/TaskItemRepositoryQuery.cs:128` (`StreamExportAsync` returns `IAsyncEnumerable<TaskItemExportDto>`) | Consumed by the export writer named in item 8; the streaming shape predates this refactor, the writer inside it changed. |
| 13 | DbContext pooling | Done (pre-existing) | `src/Host/TaskFlow.Bootstrapper/Registration/RegisterServices.Database.cs:43,58,69` (`AddPooledDbContextFactory<TaskFlowDbContextTrxn/Query/FlowEngineDbContext>`) | TickerQ's own context is unpooled by upstream limitation, per the plan; not independently re-verified in this pass since it is outside this refactor's file set. |
| 14 | Bulk ops, split queries, raw SQL where critical | Done (pre-existing, extended) | `src/Infrastructure/TaskFlow.Infrastructure.Repositories/TaskItemRepositoryQuery.cs:34` (`AsSplitQuery`); `RelationalTaskViewRepository.cs:112-122` (`ExecuteUpdateAsync` statement-bodied setter); `RelationalAuditLogRepository.cs:59-64` (`ExecuteDeleteBatchedAsync`); `PgVectorTaskEmbeddingRepository.cs:71` (`ExecuteDeleteAsync`) | The relational read-model and audit repositories added by P3 reuse the same `ExecuteUpdateAsync`/`ExecuteDeleteBatchedAsync`/upsert idioms already established for the operational work tables (D-028), rather than introducing a new bulk-op pattern. |
| 15 | Postgres features (JSONB, pgvector, partitioning) | Done / documented | `src/Infrastructure/TaskFlow.Infrastructure.Data/ReadModel/TaskItemEmbedding.cs` (`Vector Embedding`, `DefaultDimensions=1536`); `src/Infrastructure/TaskFlow.Infrastructure.AI/Search/PgVectorSearchService.cs` (cosine-distance query, tenant-scoped, `SearchMode.Semantic` only); `Directory.Packages.props:63` (`Pgvector.EntityFrameworkCore` 0.3.0) | pgvector entity is mapped only when the active provider is Npgsql (comment in `TaskItemEmbedding.cs:9-13`); no `DbSet` so the type never reaches the SQL Server model. TaskView `Document` column stays a plain string (not `jsonb`) by design (D-038, rejected `HasColumnType("jsonb")` to avoid a forced provider branch) - partitioning remains a documented customization, not built. |
| 16 | Read/write split + PgBouncer | Done | `src/Infrastructure/TaskFlow.Infrastructure.Data/Provider/TaskFlowDbProvider.cs:55-78` (`PoolerMode` enum, `PoolerModeSelector.Resolve` from `Database:PostgreSql:PoolerMode`, appends `No Reset On Close=true;Max Auto Prepare=0` at line ~174); `infra/modules/postgres-flexible-server.bicep` (pgbouncer param, per grep match) | Read/write split via the Query/Trxn context split predates this refactor (D-027); the pooler switch (D-045) is new. Portable lane runs a PgBouncer container (`deploy/compose/pgbouncer/`, seen in item watch-list checks); Azure lane uses Flexible Server's `pgbouncer.enabled` Bicep param, not available on Burstable tier. |
| 17 | Transient work tables, hard deletes | Done (pre-existing) | `src/Infrastructure/TaskFlow.Infrastructure.Data/Operational/OperationalWorkBase.cs:1-40` (`OutboxMessage`, `BlobDeleteWork`, lease fields, `ExecuteDeleteAsync`/`ExecuteDeleteBatchedAsync` completion) | Unaffected by this refactor; cited for completeness. |
| 18 | Internal gRPC | Done (internal read proof) | `src/Shared/TaskFlow.Contracts.Grpc/taskflow_read.proto`; `src/Host/TaskFlow.Api/Grpc/TaskFlowReadGrpcService.cs:37-40` (`GetTaskItemSummary`/`GetTaskMetadata`/`GetTaskItem`, `RequireAuthorization`); `src/Host/TaskFlow.Api/appsettings.json:7-15` (`Kestrel:Endpoints:Http` 8080, `Grpc` 8081 Http2) | Delegates to the same application services REST uses (comment at `TaskFlowReadGrpcService.cs:14-21`), so the two transports cannot drift; Blazor Server is the one gRPC consumer via `AddGrpcClient`, the Gateway keeps REST for public clients (D-054). Watch item: the Kestrel `Grpc`/`Http` endpoint config can conflict with `launchSettings.json`'s `applicationUrl` on local `dotnet run` (see Watch items). |
| 19 | Binary payloads | Done (scoped) | `src/Infrastructure/TaskFlow.Infrastructure.Caching/CacheSettings.cs:44-64` (`CacheSerializer.MessagePack`, contractless resolver) | Scoped to the L2 cache value only (D-048); the message-queue payload deliberately stays JSON for broker-side filtering/interop/debuggability (comment in `TaskFlowJsonContext.cs` doc header and D-048 in `DESIGN-DECISIONS.md:174`). |
| 20 | In-app rate limiting | Done | Api tenant limiter pre-existing; edge limiter added: `src/Host/TaskFlow.Gateway/EdgeRateLimitSettings.cs` (`TokensPerPeriod=200`, `ReplenishmentSeconds=1`, `QueueLimit=0`, `MaxConcurrentRequests=1000`); wired via `RateLimiting:Edge` in `src/Host/TaskFlow.Gateway/appsettings.json:34-46` | Edge limiter is in-process per replica by design (documented ceiling in `EdgeRateLimitSettings.cs:7-12`: swap to the Redis-backed partition factory when replica count makes cross-replica accounting matter). |
| 21 | Distributed locks | Done | `src/Application/TaskFlow.Application.Contracts/Locking/IDistributedLock.cs` (`TryAcquireAsync(key, ttl, ct)`); arms at `src/Infrastructure/TaskFlow.Infrastructure.Caching/Locking/RedisDistributedLock.cs`, `InProcessDistributedLock.cs` | Scoped to non-reentrant startup tasks (external resource provisioning, RabbitMQ topology declaration) per the interface doc comment; work-table coordination continues to use leases/conditional updates (D-026), not this lock. |
| 22 | Resilience pipelines (Polly v8) | Done | Standard handler pre-existing: `src/Host/Aspire/ServiceDefaults/Extensions.cs:28` (`AddStandardResilienceHandler()`); YARP hardening added by G2 (see item 5 evidence, same appsettings block) | Every outbound HTTP client already carried the standard resilience handler; this refactor's addition is the YARP cluster-level hardening (active/passive health, load balancing, timeout), not a new resilience pipeline. |
| 23 | Hedged requests | Done | `src/Host/Aspire/ServiceDefaults/ReadHedgingExtensions.cs` (`AddReadHedging`, `Resilience:Hedging` config, `ShouldHandle`/`DelayGenerator` both restricted to GET) | Comment in the file explains why both `ShouldHandle` and `DelayGenerator` must be restricted to GET: Polly can trigger a hedge either from an outcome or from the delay timer, and only gating the former would still duplicate a slow POST. Cosmos cross-region hedging (`CrossRegionHedgingStrategy`) is config-gated per the session log; not independently re-verified in this pass (deployment-only, requires a live Cosmos multi-region account). |
| 24 | OpenTelemetry (ActivitySource, Meter, LoggerMessage, W3C) | Done | `src/Shared/TaskFlow.Observability/Tracing/TaskFlowActivitySources.cs` (`TaskFlow.Messaging`, `TaskFlow.Scheduler` sources); registered at `src/Host/Aspire/ServiceDefaults/Extensions.cs:80-82` (`tracing.AddSource(...)`); LoggerMessage/CA1848 sweep at commit `0c5e250` on this branch | `MessagingTrace.cs` (found at `src/Shared/TaskFlow.Observability/Tracing/MessagingTrace.cs`) carries the W3C `traceparent` inject/extract helpers per the plan (D-053); not opened line-by-line in this pass beyond confirming its existence and the activity-source registration it depends on. LoggerMessage/CA1848 sweep (this same G4 slice): all 58 remaining raw `Log*()` call sites converted to `[LoggerMessage]`, `src/.editorconfig` sets `dotnet_diagnostic.CA1848.severity = error` for `src/**.cs`; verified with `dotnet build TaskFlow.slnx -m:1` (52 projects, 0 warnings/errors) and a temporary probe call that confirmed CA1848 fires as a build error before being removed. |
| 25 | Separated live/ready probes | Done | `src/Host/Aspire/ServiceDefaults/Extensions.cs:140-161` (`MapDefaultEndpoints`: `/healthz`, `/healthz/live` tag `live`, `/healthz/ready` tag `ready`) | `/readyz` removed per the doc comment at lines 129-139; cache is deliberately excluded from the `ready` tag (degrades to L1 rather than failing requests, stated in the same comment block). |
| 26 | Dynamic feature flags | Done | `src/Application/TaskFlow.Application.Contracts/TaskFlowFeatures.cs` (`TaskViews`, `Export`, `SemanticSearch`, `AiReview` constants); `src/Host/TaskFlow.Api/Filters/FeatureGateEndpointFilter.cs` (`RequireFeature`, answers 404 when off) | Backed by Azure App Configuration when `AppConfig:Endpoint` is set, else the `FeatureManagement` appsettings section (comment in `TaskFlowFeatures.cs:4-7`). Not wired into the Functions host (see Watch items). |

## Native AOT evaluation (D-047)

Native AOT was evaluated and not adopted for the whole application. EF Core 10's Native AOT support is
still experimental (precompiled queries only), and two of TaskFlow's scheduling/workflow dependencies -
TickerQ and FlowEngine - are reflection-based, so neither can be trimmed or AOT-compiled cleanly today.
Adopting AOT for the app as a whole would have meant forking or replacing both dependencies, which is out
of scope for this refactor. Instead the runtime profile (`src/Host/TaskFlow.Host.props`) adopts the parts
of the AOT story that do not require those dependencies to change: `ReadyToRun` publishing for the Api and
Gateway Dockerfiles (startup latency under scale-out, at the cost of image size), `TieredPGO` made explicit,
DATAS (`GarbageCollectionAdaptationMode=1`), and `EnableRequestDelegateGenerator` on the Api
(`src/Host/TaskFlow.Api/TaskFlow.Api.csproj:7`). The one project that did get `IsAotCompatible=true` is
`src/Packages/EF.Messaging.RabbitMq/EF.Messaging.RabbitMq.csproj:15` - confirmed by reading the csproj
directly: it carries no TaskFlow dependency and no reflection of its own, so it is the one piece of the
tree that can safely carry the trim/AOT contract for future consumers.

## Messaging framework decision (D-046)

TaskFlow keeps its own transactional outbox, the `IIntegrationEventTransport` port, and the in-repo
`EF.Messaging.RabbitMq` package as the messaging layer, rather than adopting a messaging framework.
MassTransit was rejected because v9 is commercial and v8's maintenance window ends in 2026; SlimMessageBus
was rejected because it would duplicate infrastructure TaskFlow already has and has already proven on both
brokers - the outbox claim/dispatch loop and the consumer host. Kafka is noted as a possible future
transport behind the same `IIntegrationEventTransport` port, but nothing was built for it: no consumer in
this codebase needs Kafka's log-retention/replay semantics today. The decision and its rejected alternatives
are recorded at `.scaffold/DESIGN-DECISIONS.md:172` (D-046).

## Watch items

- Pgvector.EntityFrameworkCore 0.3.0 targets net8.0/Npgsql >= 9.0.1 rolling forward onto 10.0.3
- Floating image tags in compose (otel-lgtm, minio, pgbouncer)
- launchSettings.json applicationUrl vs Kestrel:Endpoints warning on local dotnet run
- TaskFlow.Functions.csproj still has OpenAI PrivateAssets=all
- App Configuration source not wired into Functions
- Functions obj-tree build noise (~180 ExtensionsMetadataGenerator warnings on a second consecutive build)
- TaskItemRescheduledEvent dead code
- nothing in the compose/VPS path has executed on the dev machine (CI compose-smoke is the first real run)

Verification notes on the above, checked against this tree where practical:
- `Directory.Packages.props:63` pins `Pgvector.EntityFrameworkCore` at `0.3.0`; its own `.nuget.g.targets`
  and `deps.json` under `src/Infrastructure/TaskFlow.Infrastructure.Data.Migrations.PostgreSql/obj|bin`
  show the package's own `lib/net8.0` target.
- `deploy/compose/docker-compose.yml:168` pins `grafana/otel-lgtm:latest`; `docker-compose.yml:185` pins
  `${PGBOUNCER_IMAGE:-edoburu/pgbouncer:latest}`; `docker-compose.override.local.yml:53` pins
  `minio/minio:latest`. All three float on `latest`.
- `src/Host/TaskFlow.Api/Properties/launchSettings.json` sets `applicationUrl` to `http://localhost:5188`
  and `https://localhost:7067;http://localhost:5188`, while `src/Host/TaskFlow.Api/appsettings.json:7-15`
  now declares `Kestrel:Endpoints:Http` (8080) and `Kestrel:Endpoints:Grpc` (8081, Http2) - the two disagree
  on the plain-HTTP port for a local `dotnet run`.
- `src/Host/TaskFlow.Functions/TaskFlow.Functions.csproj:31` still carries
  `<PackageReference Include="OpenAI" PrivateAssets="all" />` (also `Microsoft.Extensions.AI.OpenAI` and
  `Microsoft.AI.Foundry.Local`, both `PrivateAssets="all"`).
- No `AppConfig`/`FeatureManagement` reference was found anywhere under `src/Host/TaskFlow.Functions`.
- The ExtensionsMetadataGenerator build-noise item is not independently reproduced in this pass (this task
  does not run `dotnet build`); it is recorded in the G2 entry of the orchestration session log
  (`docs/plans/portable-lane-guidance-refactor.md`), not in `.scaffold/REFERENCE-STATUS.md`, which predates
  this refactor (last verified 2026-09-04) and does not yet reflect it.
- `src/Domain/TaskFlow.Domain.Shared/Events/TaskItemRescheduledEvent.cs` defines the record; a repo-wide
  search for `new TaskItemRescheduledEvent(` outside `obj`/`bin` found no construction sites. This matches
  `.scaffold/REFERENCE-STATUS.md:122`, which already records it as dead code.
- The compose/VPS path itself was not exercised in this verification pass, consistent with the claim: CI's
  `compose-smoke` job (`.github/workflows/ci.yml:338-340`) is gated behind
  `workflow_dispatch` + `inputs.includeComposeSmoke == true`, so it has not run as part of ordinary pushes.
