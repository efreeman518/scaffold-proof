# REFERENCE-STATUS - TaskFlow

Canonical current evidence for the TaskFlow reference application. Historical phase narrative belongs in Git history; root `HANDOFF.md` contains only terminal routing state.

> Update this file only from observed results. TaskFlow CI records the scaffold checkout commit used for cross-repository validation so failures remain diagnosable without creating a compatibility pin.

The 2026-09-16 provider/toolchain refresh removed the deprecated local AI provider while retaining Azure AI Foundry, Azure AI Inference, and OpenAI-compatible providers. Results below distinguish passed, blocked, and not-run evidence on the refreshed dependency graph.

## Build Status

| Field | Value |
|---|---|
| Last verified | 2026-09-16 |
| Solution | `TaskFlow.slnx` |
| Target framework | .NET 10 |
| Configuration | Release |
| Solution build units (`dotnet build TaskFlow.slnx -c Release --no-restore -m:1`) | 50 projects in 1 min 45.57 s |
| Fresh Release restore | 48 projects in 10.6 s |
| Errors | 0 |
| Warnings | 0 |

`src/UI/TaskFlow.Uno/TaskFlow.Uno.csproj` builds separately because the Uno SDK requires explicit invocation. Release restore, build, and browser-WASM publish each covered 3 projects (`TaskFlow.Uno`, `TaskFlow.Uno.Core`, `TaskFlow.Uno.Presentation`) with 0 errors and 0 warnings; the final Release build took 19.28 s. The iOS target also compiled all 3 projects, but Windows cannot run the iOS app or its Appium tests.

`dotnet ef migrations has-pending-model-changes` is clean for all 6 context/provider pairs (the app `DbContext` and the FlowEngine `DbContext`, each against SqlServer and PostgreSql, plus the TickerQ context pairing where applicable).


## Test Status

### Fast matrix

All current Release fast projects passed serially with no failed, skipped, or inconclusive tests:

| Project | Passed | Duration |
|---|---:|---:|
| Test.Unit | 544 | 8.2 s |
| Test.UI | 59 | 1.3 s |
| Test.Architecture | 77 | 2.4 s |
| Test.Endpoints | 166 | 7.5 s |
| Test.Integration.FlowEngine | 18 | 0.9 s |
| Test.Mutation | 33 | 0.8 s |
| Test.PlaywrightUI (`TestCategory=Unit`) | 5 | 1.1 s |
| **Total** | **902** | |

`Test.Unit` used a 15-second blame-hang timeout. `dotnet format analyzers TaskFlow.slnx --severity warn --verify-no-changes --no-restore` passed with no changes or diagnostics.

### Component containers

The Podman Docker-compatible context ran every component lane with `--no-build --no-restore -m:1`. All 213 tests passed with no failed, skipped, inconclusive, or warning results:

| Lane | Project | Passed | Duration | Services observed |
|---|---|---:|---:|---|
| Azure / Cosmos | Test.Integration | 55 | 141.6 s | SQL Server, Azurite, Redis, Ryuk; Cosmos provider selected, but these component fixtures do not launch the emulator |
| Azure / Cosmos | Test.E2E | 10 | 36.9 s | SQL Server contract |
| NonAzure / PostgreSqlJsonb | Test.Integration | 68 | 92.7 s | PostgreSQL/pgvector, Redis, RabbitMQ, SeaweedFS, Ryuk |
| NonAzure / PostgreSqlJsonb | Test.E2E | 10 | 15.9 s | PostgreSQL and Redis |
| NonAzure / MongoDb | Test.Integration | 70 | 115.9 s | PostgreSQL/pgvector, Redis, RabbitMQ, SeaweedFS, MongoDB, Ryuk |

Ryuk cleanup completed and no run-owned containers remained.

### Browser, mobile, and full Aspire graphs

| Surface | Result | Evidence boundary |
|---|---|---|
| Full Test.PlaywrightUI | 8 passed, 0 failed/skipped/inconclusive | Blazor, React, Uno WASM, and empty-browser cold start |
| Android mobile | 3 passed, 0 failed/skipped/inconclusive | Appium Android runner |
| iOS mobile | Not run | All 3 Uno projects compiled for iOS; Windows has no runnable iOS app/test host |
| NonAzure Aspire full graph | 6 passed, 0 failed/skipped/inconclusive in 102.7 s | PostgreSQL/pgvector, RabbitMQ, SeaweedFS, Redis, migrator, scheduler, Gateway/API, Blazor, React, Uno WASM, and outbox mesh; Functions correctly absent |
| Azure Aspire full graph | Blocked | Initial exact CI filter: 0 passed, 7 failed, 3 skipped in 1064.0 s because `taskflowdb` became unhealthy before the remaining tests could execute |

The Azure failure is a reproduced Aspire SQL child-database health ordering defect: SQL Server was healthy and client-ready, the child probe attempted `taskflowdb` before Aspire's `ResourceReadyEvent` created it, SQL logged error 18456 state 38, and the database was created shortly afterward without a later successful probe. SQL Server 2022 reproduced the ordering, ruling out the SQL Server 2025 image; an isolated `Aspire.Hosting.SqlServer` 13.5.3 rollback also reproduced it. The repository therefore retains 13.5.4 and adds no sleep, retry, health suppression, or compatibility pin.

Five live Azure AI Foundry tests were not run because no external endpoint or credentials were supplied: three `AiFoundryLiveSmokeTests` cases and two `FlowEngineFoundryWorkflowTests` cases. This is not local graph evidence.

`EF.Messaging.RabbitMq.Tests` (both the unit and Testcontainers.RabbitMq integration lanes, 31 tests total) no longer exists in this repo: the package shipped and the in-repo project was deleted (request 23; see `docs/plans/ef-messaging-rabbitmq-package-spec.md`). Its coverage now lives in the published `EF.Messaging.RabbitMq` package's own test suite, outside this repo.

### Compose and application images

All 8 application images built: API, Gateway, Scheduler, DatabaseMigrator, Blazor, React, Uno, and Functions. Base plus local-override Compose configuration validated. A unique 12-service NonAzure live smoke reached readiness and passed create, read, strong-ETag delete, React configuration, and Uno configuration; cleanup left zero run-owned resources. The local CI profile intentionally excludes OpenObserve and OTLP. Verified fixes cover ReadyToRun restore properties, Python in the Uno build image, PostgreSQL 18's `/var/lib/postgresql` volume, and ETag-aware CI deletion. No VPS or Azure deployment and no load test ran.

Published Release Uno cold-start and normal browser projects pass from empty browser state without refresh, retry, sleep, or exception suppression. Browser WASM Release temporarily sets `PublishTrimmed=false` because current Uno Navigation, Toolkit, and WinUI packages emit upstream `IL2104` under warnings-as-errors. Removal condition: those packages become trim-clean.

### Tooling state

| Tool | Observed version/state |
|---|---|
| .NET SDK | 10.0.401 |
| Global Aspire CLI | 13.5.4 |
| Global Azure Developer CLI | 1.34.0 |
| Global npm / Node.js | npm 12.0.2; Node.js 24.16.0 |
| Global Appium / UiAutomator2 | 3.7.0 / 8.7.0 |
| Global Mermaid CLI / Codex | 11.17.0 / 0.154.0 |
| Global Uno.Check / Uno templates | 1.34.1 / 6.7.22 |
| Repository tools | ILSpy 11.0.0.9375; Stryker 5.0.0; dotnet-ef 10.0.12; Refitter 2.2.0 |

Machine-level updates still blocked outside the repository: installed workloads remain at manifest set 10.0.400.1 after the updater stalled twice, although required workload IDs are installed; Node.js 24.16.0 cannot move to 24.21.0 without an administrator MSI; Android updates require Google license acceptance; Functions tooling resolves 4.12.0-preview.1 because the npm 4.14 bootstrap failed with `ENOENT`; Uno.Check reports the prohibited MAUI meta-workload and a registry false positive even though required individual workloads and long paths are present.

## Vulnerability Status

Run `dotnet list package --vulnerable --include-transitive` and capture findings here. Severity policy: [scaffold execution gates](https://github.com/efreeman518/scaffold-ai/blob/main/support/execution-gates.md#vulnerability-audit).

Last audit (2026-09-16): authenticated forced Release restores with `NuGetAudit=true` and `NuGetAuditMode=all` covered 48 solution projects and 3 Uno projects with 0 warnings and no known vulnerable direct or transitive packages. React `npm ci`, audit, build, and lint also passed with 0 vulnerabilities; Vite retains its existing 640 KB chunk-size advisory.

| Package | Severity | Direct/Transitive | Advisory | Notes |
|---|---|---|---|---|
| _None_ | - | - | - | Full solution audit reported no vulnerable packages |

Central packages restored and built at the current compatible versions, including Aspire/AppHost 13.5.4, `Aspire.Azure.AI.Inference` 13.5.4-preview.1.26464.4 (no stable release), EF.* 1.1.103, EF.FilterBuilder/FlowEngine 1.0.179, OpenAI 2.14.0, Uno.Sdk 6.7.22, and Uno.Extensions 7.3.6. The final direct-outdated command hit the .NET CLI error `Sequence contains no matching element`; it did not produce a newer package finding. The successful authenticated restore/build and vulnerability audit are current evidence; the outdated listing failure remains a tooling blocker.

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
| Aspire, Gateway, Scheduler, Functions | proven except blocked Azure full graph | Build, topology, unit, endpoint, Compose, and NonAzure 6/6 full-graph evidence; Azure full graph is blocked by the reproduced Aspire SQL child-health ordering defect |
| Uno, Blazor, React | proven | Build, Test.UI, Compose smoke, and full Playwright 8/8 including Uno WASM cold start |
| FlowEngine | proven | Runtime wiring, separate-schema migration, definition/integration cases including the If-Match:* connector override (D-032) |
| GitHub Actions and deployment workflow shape | proven | Workflow contract tests and CI execution |
| Bicep module shape (SQL Server, Service Bus, Storage, Cosmos, Redis, Container Apps/Functions, scale rules) | proven | `main`, foundation, and all three parameter files compile; Bicep contract tests |
| Live Entra or CIAM sign-in | deployment-only | Scaffold auth is the local proof |
| Azure AI Foundry, Azure AI Inference, and Azure AI Search | deployment-only | Azure AI resources are externally provisioned. Configuration and provider-selection contracts pass; five live cloud tests were not run without an endpoint, deployment, and credentials |
| Key Vault backed encryption and data-protection keys | deployment-only | AppHost and Bicep wiring exist; live vault, CMK, identity, RBAC require deployment |
| 5,000 RPS load gate | deployment-only | Test.Load exists (in-house LoadRunner) but is manual |
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
| Strict hosting lanes (`TASKFLOW_LANE=Azure\|NonAzure`, D-060) | proven | `HostingLaneResolver`; `Test.Unit/Hosting/ProviderSwitchSelectorTests.cs`; `Test.Aspire/AppHostLaneTopologyTests.cs` (23 verified topology/migrator tests, including exact Azure image references). `Portable` is a deprecated alias for `NonAzure` for one release. |
| S3 object storage (D-037) | proven | `Test.Integration/S3ObjectStorageRepositoryTests.cs` (SeaweedFS Testcontainers); `Test.Unit/Infrastructure/S3StorageRegistrationTests.cs` |
| PostgreSQL JSONB read model (D-038) | proven | `Test.Integration/RelationalTaskViewRepositoryTests.cs` in the NonAzure lane; MongoDB is an explicit NonAzure alternative |
| Relational audit sink (D-039) | proven | `Test.Integration/RelationalAuditLogRepositoryTests.cs`, both DB lanes |
| pgvector semantic search (D-040) | proven | `Test.Integration/PgVectorSearchTests.cs` (PostgreSql lane only; fails fast at startup on SqlServer by design) |
| OpenAI-compatible LLM client (D-041) | proven | `Test.Unit/AI/AiProviderSelectorTests.cs` (fail-fast on missing endpoint/API key, registers both `IChatClient` and the embedding generator) |
| App Configuration + dynamic feature flags (D-042) | proven | `Test.Architecture/FeatureManagementArchitectureTests.cs`; `Test.Endpoints/FeatureFlagEndpointTests.cs` (off -> 404, on -> 200, boots without `AppConfig:Endpoint`); `Test.Unit/Hosting/TenantTargetingContextAccessorTests.cs`; Functions uses `Microsoft.Azure.AppConfiguration.Functions.Worker` |
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

1. Azure Aspire full-graph acceptance is blocked by Aspire's SQL child-database probe running before `ResourceReadyEvent` creates the database. The latest stable 13.5.4 and tested 13.5.3 behave identically. Remove this blocker only after an upstream release changes that ordering and the exact Azure/Cosmos CI filter passes all 10 selected tests without skips or inconclusive results.
2. Five live Azure AI Foundry tests require an externally provisioned endpoint, deployment, and credential. They were not run on 2026-09-16.
3. The 5,000 RPS load gate is deployment-only by design and was excluded from this refresh. No VPS or Azure deployment ran.
4. Machine-level tooling updates require actions outside this repository: finish the .NET workload manifest update, run the administrator Node.js MSI, accept Android licenses, and replace the preview Functions CLI when a working current bootstrap is available.
5. Browser WASM trimming remains disabled for Release because upstream Uno dependencies emit `IL2104` under warnings-as-errors. Remove the workaround when those packages become trim-clean; current cold-start and full-browser evidence is 8/8.
6. iOS compile proof is current, but runnable iOS Appium acceptance requires a supported macOS/Xcode host.
7. The direct package-outdated command is blocked by the .NET CLI `Sequence contains no matching element` failure. Authenticated restore, build, and vulnerability audit passed; rerun direct outdated discovery after the CLI defect is resolved.
