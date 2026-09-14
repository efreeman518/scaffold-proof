# REFERENCE-STATUS - TaskFlow

Canonical current evidence for the TaskFlow reference application. Historical phase narrative belongs in Git history; root `HANDOFF.md` contains only terminal routing state.

> Update this file only from observed results. TaskFlow CI records the scaffold checkout commit used for cross-repository validation so failures remain diagnosable without creating a compatibility pin.

This refresh includes the integrated D-061 observability lane. Numbers below include the observed local Release build and fast lane on 2026-09-13; AppHost, container, and vulnerability evidence not rerun for D-061 remains dated 2026-09-12.

## Build Status

| Field | Value |
|---|---|
| Last verified | 2026-09-13 (local Release build and fast lane; container and AppHost evidence carried from 2026-09-12) |
| Solution | `TaskFlow.slnx` |
| Target framework | .NET 10 |
| Configuration | Release |
| Solution projects declared (`dotnet sln list` / slnx `<Project Path=` count) | 48 |
| Fresh Release restore | 49 restore projects, passed |
| Solution build units (`dotnet build TaskFlow.slnx -c Release -m:1` summary line) | 51 in 41.32 s |
| Errors | 0 |
| Warnings | 0 |

`src/UI/TaskFlow.Uno/TaskFlow.Uno.csproj` builds separately (Release and Debug, 3 projects: `TaskFlow.Uno`, `TaskFlow.Uno.Core`, `TaskFlow.Uno.Presentation`) because the Uno SDK requires explicit invocation; both builds had 0 errors and 0 warnings (Release 17.17 s; Debug 21.80 s).

`dotnet ef migrations has-pending-model-changes` is clean for all 6 context/provider pairs (the app `DbContext` and the FlowEngine `DbContext`, each against SqlServer and PostgreSql, plus the TickerQ context pairing where applicable).


## Test Status

Numbers below are the observed local fast lane on 2026-09-13. The full Unit project passed **527/527** (7.8 s test time, 9.84 s wall time) with 15-second blame-hang diagnostics, safely below the 60-second CI limit. The categorized fast lane (`TestCategory=Unit|TestCategory=Architecture|TestCategory=Endpoint`, `dotnet test TaskFlow.slnx`) passed **743/743 across 13 projects** (10.7 s test time, 14.26 s wall time). The full `Test.Endpoints` project passed **167/167** (7.1 s test time, 8.84 s wall time). For historical CI evidence, run 34736624292 passed the prior **523/523** Unit count in 4 s test time and 6 s step time. `Test.UI` passed **59/59** and `Test.Integration.FlowEngine` passed **18/18** on the preceding verification pass.

Docker/Testcontainers-backed lanes (run-scoped `TESTCONTAINERS_HOST_OVERRIDE`, not committed):

| Project | Category filter | Verified count | Notes |
|---|---|---:|---|
| Test.Integration | `TestCategory=Integration` | 55 (Azure) / 68 (NonAzure) | Full container-backed observed runs; covers the lane-specific provider topology |
| Test.E2E | `TestCategory=E2E` | 1/1 targeted CRUD (Azure / NonAzure) | Targeted only; not a full Playwright claim |
| Test.Integration.FlowEngine | `TestCategory=Integration` | 18 | Includes the workflow-definition test pinning the absence of a UUIDv7 loop-iteration id (request 19 rejection) |
| Test.UI | `UI`, `Presentation` | 59 | Headless UI and presentation contracts |
| Mongo repository | targeted | 2 | Explicit NonAzure MongoDB alternative |
| NonAzure AppSurface | Aspire | 5 | Gateway, API, Blazor, React, and Uno in 99.7 s |
| Deterministic Aspire topology and migrator filter | topology | 23 | 1.4 s; includes exact Azure image references |
| Vulnerability audit | `dotnet list TaskFlow.slnx package --vulnerable --include-transitive` | all solution projects, 0 with vulnerable packages | Per-project audit over the full solution |

`EF.Messaging.RabbitMq.Tests` (both the unit and Testcontainers.RabbitMq integration lanes, 31 tests total) no longer exists in this repo: the package shipped and the in-repo project was deleted (request 23; see `docs/plans/ef-messaging-rabbitmq-package-spec.md`). Its coverage now lives in the published `EF.Messaging.RabbitMq` package's own test suite, outside this repo.

Not rerun this pass, last observed values only: **Test.Mutation** last observed 33 (mutation-target contract tests; not part of this refactor's changed surface, not rerun to save time).

Not fully rerun on this machine this pass: the full Azure Aspire mesh, the full-stack `Test.PlaywrightUI` lane, **Test.Mobile** (dedicated Appium/emulator runner), **Test.FoundryLocal** (live local-model lane, RID-bound runtime), **Test.Load** (manual; the 5,000 RPS gate is deployment-only), **Test.Benchmarks** (BenchmarkDotNet console runner, build-verified only), **compose-smoke** and **deploy-vps** (CI-only: `compose-smoke` is a `workflow_dispatch`-gated job in `ci.yml`, `deploy-vps.yml` is `workflow_dispatch`-only). Canonical JSONB, Mongo, and local Compose configurations rendered; the local service list omitted OpenObserve and its TaskFlow OTLP endpoint and headers were empty. Workflow YAML parsed with PyYAML. The OpenObserve v1.0.0 manifest was verified for linux/amd64 and linux/arm64, but no live OpenObserve container or VPS/Azure deployment ran. The bounded Azure live rerun started Cosmos and Service Bus SQL; remaining resources stayed `Created` with no `State.Error`, so the mesh is incomplete and unverified, not a manifest-duplication failure.

Published Release Uno cold-start and normal browser projects pass from empty browser state without refresh, retry, sleep, or exception suppression. Browser WASM Release temporarily sets `PublishTrimmed=false` because the current Navigation, Toolkit, and WinUI package set emits upstream `IL2104` under warnings-as-errors. Removal condition: those packages become trim-clean. Cold-start and browser evidence was not rerun this pass; this pass proves Release/Debug builds and the NonAzure static-host AppSurface only.

## Vulnerability Status

Run `dotnet list package --vulnerable --include-transitive` and capture findings here. Severity policy: [scaffold execution gates](https://github.com/efreeman518/scaffold-ai/blob/main/support/execution-gates.md#vulnerability-audit).

Last audit (2026-09-12): `dotnet list TaskFlow.slnx package --vulnerable --include-transitive` reported no vulnerable packages or advisories, direct or transitive, for any solution project. React `npm ci`, audit, build, and lint also passed with 0 vulnerabilities; Vite retains its existing 640 KB chunk-size advisory.

| Package | Severity | Direct/Transitive | Advisory | Notes |
|---|---|---|---|---|
| _None_ | - | - | - | Full solution audit reported no vulnerable packages |

## Capability Coverage

Status meanings:

- `proven`: implemented and covered by executable build, test, or smoke evidence.
- `deployment-only`: generated or wired, but live acceptance requires deployed external resources or identity.
- `CI-only`: exercised by CI (typically `workflow_dispatch`-gated), not runnable on this dev machine, but not deployment-only either.
- `documented-only`: example or opt-in documentation exists without active runtime wiring.
- `not enabled`: intentionally absent from the TaskFlow configuration.

### Pre-existing capabilities (carried forward from the prior refresh, unaffected by this refactor)

| Capability | Status | Evidence boundary |
|---|---|---|
| Service and CQRS application-style switch | proven | Shared Endpoint and E2E suites run both styles on both DB providers; `ApplicationStyleResolver` owns selection |
| Dual EF Core provider (SQL Server + PostgreSQL) | proven | `TaskFlowDbProviderSelector`/`UseTaskFlowProvider`; Unit, Integration, E2E, and migrator contracts run once per `TASKFLOW_TEST_DB_PROVIDER` value |
| Composite tenant-first PK, app-managed Version/ETag, migrations | proven | `EntityBaseConfiguration` composite `(TenantId, Id)` key; `VersionTimestampInterceptor`; two migration assemblies |
| ETag / If-Match / 412 / 428 concurrency | proven | `ConcurrencyGuard`, `IfMatchEndpointFilter`, `ETagEndpointFilter`; Endpoint and E2E cases |
| Caller-supplied UUIDv7 idempotent create | proven | `UuidV7`/`IdempotentCreateGuard`; Endpoint cases cover non-v7 400, equivalent replay 200, divergent 409 |
| Cursor paging (task items) | proven | `CursorSearchRequest`/`CursorPage`/`ICursorProtector`; Endpoint/E2E cases |
| App-layer column encryption + blind index | proven | `Infrastructure.Data/Encryption/*` on both providers; Always Encrypted (D-019) kept documented-only |
| Transactional outbox + consumer inbox | proven | `OutboxStagingInterceptor`, lease claim, `OutboxDispatcherService`; `ConsumerInbox`/`IInboxStore.TryClaimAsync` |
| Messaging transport switch (Service Bus / RabbitMQ) | proven | `Messaging:Provider`; `EF.Messaging.RabbitMq` (published package, own test suite outside this repo) + adapter tests |
| Redis cache (FusionCache) and rate limiter | proven | `EF.Cache.ITypedCache`/`CacheSettings` injected directly (app-local `ITaskFlowCache`/`FusionTaskFlowCache` deleted); `FailOpenRateLimiter` over `RedisRateLimiting` |
| Generated API clients (Refitter, openapi-typescript) | proven | `src/UI/TaskFlow.ApiClient` and React `types.ts` regenerate from the committed OpenAPI document |
| Aspire, Gateway, Scheduler, Functions | proven (Azure mesh unverified) | Build, topology, unit, endpoint coverage, plus NonAzure AppSurface; the Azure mesh remains incomplete and unverified |
| Uno, Blazor, React | proven | Build, Test.UI, and NonAzure AppSurface evidence; full-stack Playwright remains unverified |
| FlowEngine | proven | Runtime wiring, separate-schema migration, definition/integration cases including the If-Match:* connector override (D-032) |
| Foundry Local inference | proven (not rerun this pass) | Dedicated live lane requires the RID-bound Foundry Local runtime |
| GitHub Actions and deployment workflow shape | proven | Workflow contract tests and CI execution |
| Bicep module shape (SQL Server, Service Bus, Storage, Cosmos, Redis, Container Apps/Functions, scale rules) | proven | `main`, foundation, and all three parameter files compile; Bicep contract tests |
| Live Entra or CIAM sign-in | deployment-only | Scaffold auth is the local proof |
| Azure Foundry and Azure AI Search | deployment-only | Provider wiring and gated smoke tests exist; live resources not required locally |
| Key Vault backed encryption and data-protection keys | deployment-only | AppHost and Bicep wiring exist; live vault, CMK, identity, RBAC require deployment |
| 5,000 RPS load gate | deployment-only | Test.Load exists (NBomber) but is manual |
| Production infrastructure rollout | deployment-only | Deployment workflow and Bicep validated without a live rollout |
| Existing Foundry account, prompt agent, pre-existing agent opt-ins | documented-only | Commented examples only |
| Notifications | not enabled | `includeNotifications: false` |
| `azd` orchestration | not enabled | `includeAzd: false` |
| Private endpoints | not enabled | `usePrivateEndpoints: false` |

### New capabilities added by this refactor (strict NonAzure lane + scale-guidance alignment)

`TASKFLOW_LANE` owns the strict topology. `Portable` is a deprecated `NonAzure` alias for one release only; incompatible lane-owned settings fail fast.

| Lane | Owned local topology |
|---|---|
| Azure | SQL Server 2025, Service Bus emulator plus SQL Server 2022, Azurite, Cosmos |
| NonAzure | PostgreSQL 18 with JSONB read model by default, RabbitMQ 4, SeaweedFS, optional MongoDB 8 |
| Common | Redis 8, TickerQ, API, Gateway, Scheduler, DatabaseMigrator, Blazor, React, Uno |
| Azure-only | Functions |

| Capability | Status | Evidence boundary |
|---|---|---|
| Strict hosting lanes (`TASKFLOW_LANE=Azure\|NonAzure`, D-060) | proven | `HostingLaneSelector`; `Test.Unit/Hosting/ProviderSwitchSelectorTests.cs`; `Test.Aspire/AppHostLaneTopologyTests.cs` (23 verified topology/migrator tests, including exact Azure image references). `Portable` is a deprecated alias for `NonAzure` for one release. |
| S3 object storage (D-037) | proven | `Test.Integration/S3ObjectStorageRepositoryTests.cs` (SeaweedFS Testcontainers); `Test.Unit/Infrastructure/S3StorageRegistrationTests.cs` |
| PostgreSQL JSONB read model (D-038) | proven | `Test.Integration/RelationalTaskViewRepositoryTests.cs` in the NonAzure lane; MongoDB is an explicit NonAzure alternative |
| Relational audit sink (D-039) | proven | `Test.Integration/RelationalAuditLogRepositoryTests.cs`, both DB lanes |
| pgvector semantic search (D-040) | proven | `Test.Integration/PgVectorSearchTests.cs` (PostgreSql lane only; fails fast at startup on SqlServer by design) |
| OpenAI-compatible LLM client (D-041) | proven | `Test.Unit/AI/AiProviderSelectorTests.cs` (fail-fast on missing endpoint/API key, registers both `IChatClient` and the embedding generator) |
| App Configuration + dynamic feature flags (D-042) | proven | `Test.Architecture/FeatureManagementArchitectureTests.cs`; `Test.Endpoints/FeatureFlagEndpointTests.cs` (off -> 404, on -> 200, boots without `AppConfig:Endpoint`); `Test.Unit/Hosting/TenantTargetingContextAccessorTests.cs`. Not wired into the Functions host (open item) |
| Data Protection Redis persistence (D-043) | proven (selector only) | `Test.Unit/Hosting/ProviderSwitchSelectorTests.cs` proves the switch resolves and falls back correctly; actual cross-replica key-ring persistence behavior under Redis is not separately integration-tested |
| Postgres pooler mode (D-045) | proven | `Test.Unit/Infrastructure/TaskFlowDbProviderSelectorTests.cs` (`PoolerModeSelector`, connection-string flag appending) |
| Runtime profile per host (D-047) | proven | `Test.Architecture/HostRuntimeSettingsTests.cs` (every host csproj imports `TaskFlow.Host.props`) |
| Source-generated JSON contexts (D-048) | proven | `Test.Architecture/JsonContextCompletenessTests.cs`; `Test.UI/Uno/TaskFlowApiJsonContextTests.cs` |
| Health probe contract (D-049) | proven | `Test.Endpoints/HealthProbeContractTests.cs`; `Test.Unit/Gateway/GatewayHealthCheckRegistrationTests.cs` |
| Edge rate limiter + YARP active/passive health (D-050) | proven | `Test.Unit/Gateway/GatewayEdgeRateLimitTests.cs` (burst over the token bucket -> 429) |
| GET-only hedging (D-051) | proven | `Test.Unit/Hosting/ReadHedgingTests.cs` (GET hedges, POST never does); `Test.Unit/Infrastructure/CosmosHedgingOptionsTests.cs` (config-gated, deployment-only for the live behavior) |
| Distributed lock (D-052) | proven | `Test.Integration/RedisDistributedLockTests.cs` (two contenders, one wins, second wins after release); `Test.Unit/Infrastructure/InProcessDistributedLockTests.cs` (fallback) |
| Broker trace propagation (D-053) | proven | `Test.Unit/Infrastructure/BrokerTracePropagationTests.cs` (`ActivityListener` asserts a consumed message extracts the injected remote parent and creates a Consumer activity with the same trace ID and expected parent span ID, preserving a contiguous trace) |
| LoggerMessage sweep + CA1848 (D-053) | proven | `src/.editorconfig` (`dotnet_diagnostic.CA1848.severity=error` for `src/**.cs`); full solution build 0 warnings/errors after all 58 raw call sites converted |
| Internal gRPC read service (D-054) | proven | `Test.Endpoints/TaskFlowReadGrpcTests.cs` (in-memory `GrpcChannel` parity with the REST summary); `Test.Unit/Contracts/TaskFlowReadGrpcMapperTests.cs`; `Test.Architecture/GrpcArchitectureTests.cs` (gRPC service lives only in the Api host) |
| MessagePack L2 cache serializer (D-048/D-056) | proven | `Test.Unit/Infrastructure/CacheSerializerTests.cs` (round trip, both serializers); `Test.Integration/MessagePackCacheTests.cs` (L2 Redis round trip) |
| Compose lane (Docker Compose + Caddy) + VPS deploy workflow (D-036) | proven (Compose); CI-only (VPS deploy) | Canonical and local JSONB/Mongo Compose shapes pass; worker also validated eight none, Mongo, pooler, and combined shapes. `deploy-vps.yml` remains `workflow_dispatch` deploy/rollback evidence only. |
| NonAzure deployment observability (D-061) | proven (wiring and contracts); deployment-only (live runtime) | `Test.Unit/Hosting/OpenTelemetryMetricsRegistrationTests.cs` proves the runtime metrics switch; `Test.Endpoints/GlobalExceptionHandlerTests.cs` proves server `requestId` plus W3C `traceId`/`spanId` exception correlation; endpoint contracts prove the same correlation fields on typed errors; `Test.Unit/Infrastructure/DeploymentWorkflowContractTests.cs` proves OpenObserve isolation, credentials, OTLP, retention, and deploy gates. `deploy/compose/docker-compose.yml`, `docker-compose.override.local.yml`, and `.github/workflows/deploy-vps.yml` wire deployed OpenObserve OSS with metrics export disabled by default; local Aspire retains its Dashboard defaults. Compose shapes and workflow YAML validated, but no live OpenObserve container or deployment ran. |

The declared flags and matrix must agree with `.scaffold/resource-implementation.yaml`. Proof paths are validated against the scaffold-owned [TaskFlow proof map](https://github.com/efreeman518/scaffold-ai/blob/main/support/taskflow-proof-map.md).

## Phase Completion

Phases 1 through 5e and the FlowEngine extension are complete. Root `HANDOFF.md` records `workflowStatus: complete`, `currentPhase: 5`, and `currentSubPhase: complete`. The strict NonAzure-lane + scale-guidance-alignment refactor (P1-P7, G1-G4) is an ordinary-maintenance addition on top of that completed baseline, not a phase re-open.

## Infrastructure as Code

`infra/` contains the Bicep deployment baseline: top-level `main.bicep`, resource modules, deployment scripts, and rollback contracts. `deploy/compose/` contains the strict NonAzure Docker Compose baseline (Caddy, PgBouncer, SeaweedFS, images.env, VPS runbook) - see `deploy/compose/README.md`.

Deployment plan: [`.azure/deployment-plan.md`](../.azure/deployment-plan.md).

Validate locally with `az bicep build --file infra/main.bicep` and `docker compose -f deploy/compose/docker-compose.yml config -q`.

## Outstanding Follow-Ups

1. The full Azure Aspire mesh and full-stack `Test.PlaywrightUI` lane are unverified. A bounded Azure live rerun started Cosmos and Service Bus SQL; remaining resources stayed `Created` with no `State.Error`, so this is incomplete rather than a manifest-duplication failure. Targeted Azure and NonAzure CRUD E2E is not full browser acceptance.
2. EF.* package requests from `docs/plans/ef-package-requests.md`: all REQUIRED items landed as of EF.* 1.1.102 / EF.FlowEngine.*/EF.FilterBuilder 1.0.173, except request 3 (partial - EF.Data.SqlServer split shipped, `Microsoft.Data.SqlClient` stays transitive until EF.Data 2.0, open by design) and request 4 (landed, not adopted - D-004, no repository asks for NOLOCK). Request 19 (a UUIDv7-shaped FlowEngine loop iteration id) is rejected for now: `LoopNodeExecutor.IterationId` is a deterministic UUIDv5 and GR-17 rejects a non-UUIDv7 client create id with 400, so the decomposer loop keeps its own `idempotencyKey` instead. `EF.Audit.Data` and `EF.Audit.AzureTable` (published 1.1.101/1.1.102) are rejected: both stamp the consumer clock into `RecordedUtc`/`PartitionKey`/`RowKey`, reproducing the A1 replay-duplicate defect, and `EF.Audit.Data` additionally mandates its own `AuditDbContext`; reconciliation stays contracts-only via `EF.Audit.Contracts`, with the id-derived key scheme (D-058) implemented app-side. See the dated "Feedback after adoption (2026-09-10)" section in `ef-package-requests.md` for the remaining post-merge findings (KeysetProjection selector shape, envelope tenant slot, sender-pool enumeration, envelope serializer JsonTypeInfo support, trace-context Baggage, EF.Messaging's Azure SDK footprint).
3. The 5,000 RPS load gate is deployment-only by design (`Test.Load` is manual/NBomber); local proof is Testcontainers plus the million-row fixture, not a live RPS measurement.
4. Authenticated Azure AI persistence and enqueue scenarios remain deployment-only.
5. Browser WASM trimming remains disabled for Release because upstream Uno dependencies emit `IL2104` under warnings-as-errors. Trimming and cold-start browser evidence was not rerun; Release and Debug builds plus the NonAzure static-host AppSurface passed.
6. `TaskItemRescheduledEvent` remains defined and versioned in the envelope map but never constructed anywhere in the merged code - still dead code, still not removed.
7. Watch items recorded by `docs/plans/scale-guidance-alignment.md` and the G3/P6 session log. Resolved by slice F1 (2026-09-09): `launchSettings.json`'s `applicationUrl` no longer conflicts with `Kestrel:Endpoints` on a local `dotnet run` (the Api's `applicationUrl` and `https` profile were removed, `Kestrel:Endpoints` is the single port source); `TaskFlow.Functions.csproj` no longer marks `OpenAI`/`Microsoft.Extensions.AI.OpenAI` `PrivateAssets="all"` (both are deployed dependencies, matching Api/Bootstrapper; `Microsoft.AI.Foundry.Local` keeps it, unrelated); App Configuration is now wired into the Functions host via `Microsoft.Azure.AppConfiguration.Functions.Worker`. Still open: `Pgvector.EntityFrameworkCore` 0.3.0 is the newest published release (verified 2026-09-09 against nuget.org), targets `net8.0`, declares `Npgsql.EntityFrameworkCore.PostgreSQL >= 9.0.1`, and runs on EF Core 10/Npgsql 10.0.3 by framework roll-forward - `PgVectorSearchTests` (PostgreSql lane) is the running proof; upgrade path is to bump the pin when a net10/EF Core 10 build ships, or vendor the type-mapping plugin in-repo if the roll-forward ever breaks. Also still open: a second consecutive solution build emits ~180 `ExtensionsMetadataGenerator` warnings from a stale `TaskFlow.Functions` `obj` tree (clean-`obj` build is 0 warnings).
