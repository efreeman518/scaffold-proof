# REFERENCE-STATUS - TaskFlow

Canonical current evidence for the TaskFlow reference application. Historical phase narrative belongs in Git history; root `HANDOFF.md` contains only terminal routing state.

> Update this file only from observed results. TaskFlow CI records the scaffold checkout commit used for cross-repository validation so failures remain diagnosable without creating a compatibility pin.

TaskFlow runs on the EF.Packages 2.0 platform packages (every `EF.*` platform id at 2.0.130; EF.FilterBuilder and the EF.FlowEngine ids at 1.0.235). Every NuGet package in `Directory.Packages.props`, the repository tools and the npm packages are on their latest stable release (2026-10-09), except `typescript` (held on ~6.0.3: typescript-eslint 8.71.1 accepts typescript >=4.8.4 <6.1.0) and `@types/node` (24.19.2, the Node 24 line CI runs, not 26.x); `Aspire.Azure.AI.Inference` is on its 13.6.1 preview line (no stable release). Results below distinguish passed, blocked, and not-run evidence; each table names its run date.

## Build Status

| Field | Value |
|---|---|
| Last verified | 2026-10-09 (Release `--no-incremental` build, vulnerability audit, Test.Unit, Test.Architecture, Test.Endpoints, Test.Integration.FlowEngine, Test.Integration on all three lanes, `AppHostLaneTopologyTests`, `ComplianceCheckSchedulerSmokeTests` and the Playwright React project, on EF.* 2.0.130 and EF.FlowEngine 1.0.235); 2026-10-05 (Test.UI, Test.Mutation; Uno build on EF.FlowEngine 1.0.204); 2026-09-29 (Aspire topology and core-lane meshes on both lanes, NonAzure full-lane graph, Test.PlaywrightUI on both lanes, Test.Load on the NonAzure dev stack); 2026-09-16 (mobile, images, deployment lanes) |
| Solution | `TaskFlow.slnx` (47 projects) |
| Target framework | .NET 10 |
| Configuration | Release |
| Release restore (`dotnet restore TaskFlow.slnx -p:Configuration=Release`) | 47 projects in 10.3 s |
| Solution build (`dotnet build TaskFlow.slnx -c Release --no-restore -m:1`) | 2 min 13.89 s |
| Errors | 0 |
| Warnings | 0 |

`src/UI/TaskFlow.Uno/TaskFlow.Uno.csproj` builds separately because the Uno SDK requires explicit invocation: the 2026-09-29 Release build covered 3 projects (`TaskFlow.Uno`, `TaskFlow.Uno.Core`, `TaskFlow.Uno.Presentation`) with 0 errors and 0 warnings in 15.94 s. The iOS target compiled all 3 projects on 2026-09-16, but Windows cannot run the iOS app or its Appium tests.

A Debug restore of the Uno project followed by a Release `--no-restore` build fails with `UNOB0019` (the DevServer targets are imported for a Debug restore); restore in the configuration you build, as CI does.

`dotnet ef migrations has-pending-model-changes` is clean for all 6 context/provider pairs (TaskFlow, FlowEngine and TickerQ, each against SqlServer and PostgreSql; `dotnet ef migrations has-pending-model-changes` per pair and `MigrationModelContractTests` in Test.Unit, 2026-10-09 against EF Core 10.0.12 and EF.FlowEngine 1.0.235). Each pair has exactly one initial migration (D-025, `MigrationBaselineArchitectureTests`); the TaskFlow context's `InitialCreate` was regenerated on 2026-10-03 for the `Attachment.StorageKey` column (D-075, max 400), the FlowEngine context's `InitialFlowEngine` on 2026-10-09 for the EF.FlowEngine 1.0.235 tag table and `Executions` columns (a dev volume created before it is dropped, README), and `DatabaseMigratorIntegrationTests` applies all targets to an empty database on PostgreSQL and SQL Server.

## Test Status

### Fast matrix

Release `--no-build` after a `--no-incremental` Release solution build (0 warnings, 0 errors), run serially; 2026-10-09 (EF.* 2.0.130, EF.FlowEngine and EF.FilterBuilder 1.0.235) for Test.Unit, Test.Architecture, Test.Endpoints and Test.Integration.FlowEngine; 2026-10-05 for Test.UI and Test.Mutation; 2026-09-29 for Test.PlaywrightUI. No failed, skipped, or inconclusive tests:

| Project | Passed | Duration |
|---|---:|---:|
| Test.Unit | 743 | 12 s |
| Test.UI | 54 | 1 s |
| Test.Architecture | 87 | 2 s |
| Test.Endpoints | 297 | 13 s |
| Test.Integration.FlowEngine | 40 | 0.3 s |
| Test.Mutation | 27 | 0.1 s |
| Test.PlaywrightUI (`TestCategory=Unit`) | 1 | 0.1 s |
| **Total** | **1249** | |

`Test.Unit` used the CI 15-second blame-hang timeout. The Release build is the analyzer gate: `TreatWarningsAsErrors` fails it on any warning-severity diagnostic. `RedisConfiguredButUnreachable_LimiterFailsOpen` bounds fail-open latency with a production-shaped connection string (373 ms). Types the EF.* packages own are tested by the package suites, not here.

### Component containers

Release `--no-build`, Podman Docker-compatible context; Test.Integration 2026-10-09 on all three lanes (EF.* 2.0.130, EF.FlowEngine 1.0.235), Test.E2E 2026-09-29:

| Lane | Project | Passed | Skipped | Duration |
|---|---|---:|---:|---:|
| `TASKFLOW_LANE=NonAzure` / PostgreSqlJsonb | Test.Integration | 166 | 5 (Azure-only) | 110 s |
| `TASKFLOW_LANE=Azure` / Cosmos | Test.Integration | 152 | 19 (NonAzure-only) | 183 s |
| NonAzure / `TASKFLOW_READMODEL_PROVIDER=MongoDb` | Test.Integration | 168 | 3 | 113 s |
| unset (resolves NonAzure) | Test.E2E | 10 | 0 | 15 s |
| `TASKFLOW_LANE=Azure` | Test.E2E | 10 | 0 | 33 s |

Isolated PostgreSQL test databases are unpooled (`TestDatabaseContainer.UnpooledPostgreSql`): with pooling, idle connections to finished tests' databases reached 86 of the container's 100 `max_connections` and a MongoDB-lane run failed with 53300 "too many clients"; unpooled, a sampled MongoDB-lane run peaked at 5 client connections.

Under CPU saturation (one busy loop per logical core, 16 cores, 2026-10-05, EF.FlowEngine 1.0.207), `AiWorkflowIntegrationTests.ComplianceCheck_ScansOnlyTheStartedTenant_AndRemindsTheTaskWithExpiringEvidence` passes 10 of 10 runs on the PostgreSQL lane, each with exactly one evidence read, one agent prompt and one reminder comment: the engine saves a new instance already claimed and refuses a second acquire of an unexpired lease from any claimant, so the sweep and a start or resume path never execute one instance at once.

### Aspire graphs

2026-09-29, Release `--no-build -m:1`. Mesh totals include the topology contracts, which run in the same project:

| Lane | Scope | Passed | Skipped | Failed | Notes |
|---|---|---:|---:|---:|---|
| none | CI topology contracts (`AppHostLaneTopologyTests`, `AppHostMigratorTopologyTests`) | 41 | 0 | 0 | No containers; includes the persistent-graph contracts (Postgres volume mount, IPv4 loopback endpoints, load profile) |
| NonAzure / PostgreSqlJsonb | CI core-lane mesh (Foundry, Functions, React and WASM off) | 59 | 12 | 0 | Skips: 5 Foundry, 3 Azure Table audit (Azure-only), 2 React/Uno opted out, 2 outbox mesh (no Scheduler in the CI mesh graph) |
| Azure / Cosmos | CI core-lane mesh (same switches) | 61 | 10 | 0 | Skips: 5 Foundry, 2 React/Uno opted out, 1 Functions audit (Functions off), 2 outbox mesh. The first attempt failed 10 tests in class initialization: the container runtime created the SQL Server, Service Bus, Azurite and Redis containers but never started them while host CPU was saturated by other sessions' builds, and Aspire reported `taskflowdb` FailedToStart. The rerun with resource logging passed with no code change |
| NonAzure / PostgreSqlJsonb | Full-lane acceptance filter (`AppSurfaceAspireTests`, `OutboxMeshTests`; Scheduler and Uno WASM on, React off) | 5 | 2 | 0 | Gateway, ETag through YARP, Blazor, Uno WASM and the outbox mesh (every consumer records the event exactly once). Skips: React (no `node_modules`), RabbitMQ dead-lettering (covered by the `EF.Messaging.RabbitMq` suite by design) |

The Azure full-lane acceptance filter (Functions on) and the Azure Foundry live tests were not run on 2026-09-29.

### Browser, mobile, and load

| Surface | Result | Evidence boundary |
|---|---|---|
| Test.PlaywrightUI, NonAzure / PostgreSqlJsonb (2026-10-09, @playwright/test 1.64.0, Microsoft.Playwright 1.63.0) | 1 passed, 0 failed/skipped | `ReactTypeScriptProject_Passes` with `TASKFLOW_PLAYWRIGHT_TESTS_ENABLED=true` after `npm ci` in both packages (2 min 6 s) |
| Test.PlaywrightUI, NonAzure / PostgreSqlJsonb (2026-10-05, Refit 16.3.0, MudBlazor 9.11.0) | 4 passed, 0 failed/skipped | `BlazorTypeScriptProject_Passes` (with the C# Gateway/Blazor smoke), `UnoWasmCanvasSmoke_Passes` and the WASM host contract in one run; `ReactTypeScriptProject_Passes` in a second run, once `npm ci` had installed `src/UI/TaskFlow.React` (without it the React project is reported unavailable and the test fails). The Release Uno cold start was not run |
| Test.PlaywrightUI, NonAzure / PostgreSqlJsonb (2026-09-29) | 4 passed, 0 failed/skipped | Blazor, React, Uno WASM and the published Release Uno cold start; the TypeScript projects ran 18 tests (1, 5, 6 and 6), all passed, in 8 min 37 s |
| Test.PlaywrightUI, Azure / Cosmos (2026-09-29) | 4 passed, 0 failed/skipped | Same projects and 18 TypeScript tests, all passed, in 11 min 21 s |
| Android mobile (2026-09-16) | 3 passed | Appium Android runner, pre-adoption package set |
| iOS mobile | Not run | All 3 Uno projects compiled for iOS; Windows has no runnable iOS app/test host |
| Test.Load (2026-09-29) | 2 passed | Against the live NonAzure dev stack (`TASKFLOW_ASPIRE_LOAD_PROFILE=true`, restarted over its persistent containers and volumes; API at `http://localhost:8080`). Task search, 1 request/s for 30 s: 30 offered, 30 succeeded, 0 dropped, p50 13 ms, p95 20 ms, p99 23 ms. CRUD workflow (search, create, get, update, delete, verify), 1 cycle/s for 90 s: 90 offered, 90 succeeded, 0 dropped, p50 66 ms, p95 83 ms, p99 91 ms. Local smoke only: the 5,000 RPS gate stays deployment-only (`tests/Test.Load/README.md`) |

Five live Azure AI Foundry tests are not run without an external endpoint and credentials: three `AiFoundryLiveSmokeTests` cases and two `FlowEngineFoundryWorkflowTests` cases.

`EF.Messaging.RabbitMq` is a published package with its own unit and Testcontainers.RabbitMq suites in the EF.Packages repository. The same holds for every capability the EF.* packages own: their behavior is proven by the package suites, and TaskFlow's tests prove the app composition.

### Compose and application images

2026-09-16, pre-adoption package set: all 8 application images built (API, Gateway, Scheduler, DatabaseMigrator, Blazor, React, Uno, and Functions); base plus local-override Compose configuration validated; a unique 12-service NonAzure live smoke reached readiness and passed create, read, strong-ETag delete, React configuration, and Uno configuration, with zero run-owned resources left. The local CI profile intentionally excludes OpenObserve and OTLP. No VPS or Azure deployment and no load test ran. Not rerun on the 2.0 package set.

Browser WASM Release sets `PublishTrimmed=false` because current Uno Navigation, Toolkit, and WinUI packages emit upstream `IL2104` under warnings-as-errors. Removal condition: those packages become trim-clean.

### Cross-repository validation

2026-09-29: `scaffold-ai/scripts/validate-reference.py --reference-root .` (scaffold-ai `e5743d2`) reports 35 issues, identical with and without this branch's documentation changes. Each names a TaskFlow file the adoption deleted (for example `StrictEnum.cs`, `OutboxDispatcherService.cs`, `GlobalExceptionHandler.cs`, `TokenService.cs`, `Test.Support/LoadRunner.cs`) or a feature sentinel string the package calls replaced (`ProtectKeysWithAzureKeyVault`, `MapHealthChecks("/healthz/live"`, `AddOtlpExporter`). The scaffold-owned TaskFlow proof map and sentinels must move to the package call sites before the CI "Validate against latest scaffold" step passes.

### Tooling state

| Tool | Observed version/state |
|---|---|
| .NET SDK | 10.0.401 (2026-09-29) |
| Container runtime | Podman 6.0.2 behind the Docker 29.5.3 client, WSL mirrored networking (2026-09-29) |
| Global Azure Developer CLI | 1.34.0 (2026-09-16) |
| Global npm / Node.js | npm 12.0.2; Node.js 24.16.0 |
| Global Appium / UiAutomator2 | 3.7.0 / 8.7.0 (2026-09-16) |
| Global Mermaid CLI / Codex | 11.17.0 / 0.154.0 (2026-09-16) |
| Global Uno.Check / Uno templates | 1.34.1 / 6.7.22 (2026-09-16) |
| Azure Functions Core Tools | 4.12.0-preview.1 |
| Bicep CLI (via `az bicep`) | 0.42.1; `az bicep build --file infra/main.bicep` reproduces the committed `infra/main.json` byte for byte apart from line endings |
| Repository tools | ILSpy 11.1.0.9782; Stryker 5.0.0; dotnet-ef 10.0.12; Refitter 2.3.0 |

Machine-level updates still blocked outside the repository: installed workloads remain at manifest set 10.0.400.1 after the updater stalled twice, although required workload IDs are installed; Node.js 24.16.0 cannot move to 24.21.0 without an administrator MSI; Android updates require Google license acceptance; Functions tooling resolves 4.12.0-preview.1 because the npm 4.14 bootstrap failed with `ENOENT`; Uno.Check reports the prohibited MAUI meta-workload and a registry false positive even though required individual workloads and long paths are present.

## Vulnerability Status

Run `dotnet list package --vulnerable --include-transitive` and capture findings here. Severity policy: [scaffold execution gates](https://github.com/efreeman518/scaffold-ai/blob/main/support/execution-gates.md#vulnerability-audit).

Last audit (2026-10-09, EF.* 2.0.130, EF.FlowEngine and EF.FilterBuilder 1.0.235): `dotnet list TaskFlow.slnx package --vulnerable --include-transitive` against nuget.org and the private feed reported no vulnerable packages for all 47 solution projects, the 3 Uno projects included. `npm audit` in `src/UI/TaskFlow.React` and `tests/Test.PlaywrightUI` (2026-10-09) reports 0 vulnerabilities in each.

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
| Composite tenant-first PK, app-managed Version/ETag, migrations | proven | `EF.Data.TenantEntityTypeConfiguration` composite `(TenantId, Id)` key; `EF.Data.DbContextBase` Version and timestamp stamping; two migration assemblies |
| ETag / If-Match / 412 / 428 concurrency | proven | `EF.Data.Contracts.ConcurrencyGuard`, `EF.AspNetCore.Concurrency` (`RequireIfMatch`, `WithETag`); Endpoint and E2E cases |
| Caller-supplied UUIDv7 idempotent create | proven | `EF.Common.Contracts.UuidV7`/`IdempotentCreateGuard`; Endpoint cases cover non-v7 400, equivalent replay 200, divergent 409 |
| Cursor paging (task items) | proven | `CursorSearchRequest`/`CursorPage`/`ICursorProtector`; Endpoint/E2E cases |
| App-layer column encryption + blind index | proven | `Infrastructure.Data/Encryption/*` on both providers; Always Encrypted (D-019) kept documented-only |
| Transactional outbox + consumer inbox | proven | `EF.Data.Outbox` (`AddOutbox`, `AddOutboxDispatcher`, `AddInbox`, lease claim) mapped into the TaskFlow write context; `EF.Messaging` `IntegrationEventConsumerBase` over the `ConsumerInbox` table; `Test.Integration/OutboxClaimTests.cs`, `InboxStoreTests.cs`; `Test.Aspire/OutboxMeshTests.cs` (NonAzure full lane) |
| Messaging transport switch (Service Bus / RabbitMQ) | proven | `Messaging:Provider`; `EF.Messaging.RabbitMq` (published package, own test suite outside this repo) + adapter tests |
| Redis cache (FusionCache) and rate limiter | proven | `EF.Cache.ITypedCache` over one shared Redis multiplexer (`AddTypedCache`); `EF.RateLimiting` tenant budgets with `EF.RateLimiting.Redis` fail-open over the same multiplexer; `Test.Integration/RedisCacheAndLimiterTests.cs` (Redis cache and limiter over the shared multiplexer); `TenantRateLimitingCompositionTests` |
| Generated API clients (Refitter, openapi-typescript) | proven | `src/UI/TaskFlow.ApiClient` and React `types.ts` regenerate from the committed OpenAPI document |
| Aspire, Gateway, Scheduler, Functions | proven except blocked Azure full graph | Build, topology, unit, endpoint and Compose evidence; core-lane meshes pass on both lanes and the NonAzure full-lane filter passes 5 with 2 by-design skips (2026-09-29); the Azure full graph (Functions on) was last observed blocked by the Aspire SQL child-health ordering defect (2026-09-16) |
| Uno, Blazor, React | proven | Build, Test.UI, Compose smoke, and Test.PlaywrightUI 4/4 on both lanes including the published Release Uno cold start (2026-09-29) |
| FlowEngine | proven | Runtime wiring, separate-schema migration, definition/integration cases including the If-Match:* connector override (D-032); node retry ownership: every `taskflow-api` node, loop-body nodes included, declares an exponential `retryPolicy` with no 412 and the definitions raise no structured `GetWarnings` warning (`WorkflowDefinitionValidityTests`), every shipped clientRef resolves against the application's registrations on both lanes and an unregistered one is reported (`FlowEngineClientRegistrationTests`), compliance-check runs end to end for one tenant only, the attachment document store serves evidence only to an instance of the attachment's tenant (it refuses another tenant's attachment and an instance with no tenant, so an admin-route start, which carries no tenant, is refused its evidence), and a suspended instance holds no claim and resumes well inside one 30 s lease (`AiWorkflowIntegrationTests`), a 503 PATCH is resent and a 412 PATCH is not (`FlowEngineWorkflowTests`) |
| GitHub Actions and deployment workflow shape | proven | Workflow contract tests and CI execution |
| Bicep module shape (SQL Server, Service Bus, Storage, Cosmos, Redis, Container Apps/Functions, scale rules) | proven | `main`, foundation, and all three parameter files compile; Bicep contract tests |
| Live Entra or CIAM sign-in | deployment-only | Scaffold auth is the local proof |
| Azure AI Foundry, Azure AI Inference, and Azure AI Search | deployment-only | Azure AI resources are externally provisioned. Configuration and provider-selection contracts pass; five live cloud tests were not run without an endpoint, deployment, and credentials |
| Key Vault backed encryption and data-protection keys | deployment-only | AppHost and Bicep wiring exist; live vault, CMK, identity, RBAC require deployment |
| 5,000 RPS load gate | deployment-only | Test.Load (`EF.Testing.Load.LoadRunner`) is manual; its local smoke passed on the NonAzure dev stack on 2026-09-29 (see Browser, mobile, and load) |
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
| App Configuration + dynamic feature flags (D-042) | proven | `EF.Host` `AddEfAzureAppConfiguration` (sentinel `TaskFlow:Sentinel`, background refresher); `Test.Architecture/FeatureManagementArchitectureTests.cs`; `Test.Endpoints/FeatureFlagEndpointTests.cs` (off -> 404, on -> 200, boots without `AppConfig:Endpoint`); `Test.Unit/Hosting/TenantTargetingContextAccessorTests.cs` |
| Data Protection Redis persistence (D-043) | proven (selector only) | `Test.Unit/Hosting/ProviderSwitchSelectorTests.cs` proves the switch resolves and falls back correctly; actual cross-replica key-ring persistence behavior under Redis is not separately integration-tested |
| Postgres pooler mode (D-045) | proven | `Test.Unit/Infrastructure/TaskFlowDbProviderSelectorTests.cs` (`PoolerModeSelector`, connection-string flag appending) |
| Runtime profile per host (D-047) | proven | `Test.Architecture/HostRuntimeSettingsTests.cs` (every host csproj imports `TaskFlow.Host.props`) |
| Source-generated JSON contexts (D-048) | proven | `Test.Architecture/JsonContextCompletenessTests.cs`; `Test.UI/Uno/TaskFlowApiJsonContextTests.cs` |
| Health probe contract (D-049) | proven | `Test.Endpoints/HealthProbeContractTests.cs`; `Test.Unit/Gateway/GatewayHealthCheckRegistrationTests.cs` |
| Edge rate limiter + YARP active/passive health (D-050) | proven | `Test.Unit/Gateway/GatewayEdgeRateLimitTests.cs` (burst over the `EF.RateLimiting` edge token bucket -> 429) |
| GET-only hedging (D-051) | proven (package) | `EF.Http.Resilience` `AddReadHedging` on the Blazor read client and `EF.CosmosDb` `CosmosClientOptionsFactory` (`Cosmos:Client:HedgingEnabled`, off by default); the GET/HEAD-only and per-attempt snapshot behavior is proven by the package suite; the live Cosmos behavior is deployment-only |
| Distributed lock (D-052) | proven | `Test.Integration/RedisDistributedLockTests.cs` (two contenders, one wins, second wins after release); `Test.Unit/Infrastructure/InProcessDistributedLockTests.cs` (fallback) |
| Broker trace propagation (D-053) | proven | `Test.Unit/Infrastructure/BrokerTracePropagationTests.cs` (`ActivityListener` asserts a consumed message extracts the injected remote parent and creates a Consumer activity with the same trace ID and expected parent span ID, preserving a contiguous trace) |
| LoggerMessage sweep + CA1848 (D-053) | proven | `src/.editorconfig` (`dotnet_diagnostic.CA1848.severity=error` for `src/**.cs`); full solution build 0 warnings/errors after all 58 raw call sites converted |
| Internal gRPC read service (D-054) | proven | `Test.Endpoints/TaskFlowReadGrpcTests.cs` (in-memory `GrpcChannel` parity with the REST summary); `Test.Unit/Contracts/TaskFlowReadGrpcMapperTests.cs`; `Test.Architecture/GrpcArchitectureTests.cs` (gRPC service lives only in the Api host) |
| MessagePack L2 cache serializer (D-048/D-056) | proven | `Test.Unit/Infrastructure/CacheSerializerTests.cs` (round trip, both serializers); `Test.Integration/MessagePackCacheTests.cs` (L2 Redis round trip) |
| Compose lane (Docker Compose + Caddy) + VPS deploy workflow (D-036) | proven (Compose); CI-only (VPS deploy) | Canonical and local JSONB/Mongo Compose shapes pass; worker also validated eight none, Mongo, pooler, and combined shapes. `deploy-vps.yml` remains `workflow_dispatch` deploy/rollback evidence only. |
| Load runner (D-062) | proven (runner); deployment-only (load gate) | `EF.Testing.Load.LoadRunner`; `Test.Unit/Load/LoadRunnerTests.cs` (the runner contract TaskFlow relies on); `Test.Load/TaskItemLoadTests.cs` scenarios assert error rate and p95/p99 and stay manual (CRUD sends If-Match on update and delete) |
| No unsafe-method retry (D-063) | proven | `Test.Unit/Hosting/ServiceDefaultsScaleTests.cs` (transient 503: POST sent once, GET retried three times; the FlowEngine `taskflow-api` client, registered through `AddResilientHttpClient` under ServiceDefaults, sends a GET once); Blazor clients inherit the ServiceDefaults handler with header propagation off, the read-only gRPC client keeps retries |
| Api error mapping (D-066) | proven | `Test.Endpoints/GlobalExceptionHandlerTests.cs` (client abort 499, uncaused cancellation or timeout 504, framework faults 500, `InvalidRequestException`/`InvalidCursorException` 400, mapped exceptions their status, no 5xx detail outside Development); `TaskItemEndpointTests.Given_InvalidPayload_When_PutUpdate_Then_Returns400`; `CategoryServiceTests`/`CqrsFailureMappingTests` (fixed save message, cancellation propagates, create race replays or 409) |
| System request context (D-067) | proven | `Test.Unit/Hosting/SystemRequestContextTests.cs` (no request resolves the system identity, anonymous HTTP request, a token claiming `System` does not get it, a real GlobalAdmin keeps its role, the package system context carries only `System`, which passes the real `AddTenantBoundary` registration (`CrossTenantRoles = [GlobalAdmin, System]`) for the tenant the data names, background comment write saves); `TenantTargetingContextAccessorTests.GetContextAsync_SequentialRequestsFromDifferentTenants_TargetsEachTenant` |
| Save classification and fresh-read retry for child adds and `If-Match: *` writes (D-073, D-032) | proven | `Test.Integration/ChildAddConcurrencyTests.cs` on PostgreSQL and SQL Server: an interceptor commits a competing comment before the first save of a comment add, checklist add or tag association (service and CQRS); the add is saved, the root version moves twice and the response reports the retried version; a competing insert of the same comment id, checklist item id or tag association replays the stored row; a competing write before every attempt ends in 409 after three saves. `Test.Integration/WildcardWriteConcurrencyTests.cs` on both providers stages the same race for a wildcard root PATCH, PUT and DELETE and a comment PUT (service and CQRS): each re-reads and applies on top of the competing write (the PUT replaces the fresh child set), the root version moves twice, a competing write before every attempt ends in 409 after three saves, and a concrete If-Match that goes stale before the save still answers 412 after one save. Before each fix the cases failed on both providers (412 `expected version 1, current 2`; 400 `The change could not be saved.` for the same key; 412 instead of 409 on exhaustion; the wildcard cases 10 of 12 on each provider, the 2 concrete-version guards passing); `Test.Integration/CategoryDeleteAtomicityTests.cs` on both providers fails the Category delete's save after the task detach and checks the tasks keep their category (before the fix 2 of 2 failed on each provider: the detach had committed), and makes a wildcard Category delete lose a race to a category edit and retry; `Test.Unit/Cqrs/CqrsConcurrencyRetryTests.cs` pins the CQRS adds and all 14 CQRS If-Match writes to the wrapper (wildcard: one save inside it; concrete: one save, no retry); `ConcurrencyArchitectureTests` keeps every Application save on `Throw`. `Test.Integration/WildcardDeleteLandedCommitTests.cs` on both providers throws a provider-transient exception right after a wildcard Category or Attachment delete's write is durable: the retry that finds the row gone still evicts the category cache, the attachment's landed commit leaves exactly one staged blob-delete row (the re-sent save's failure is classified as a lost save once the row is gone), and a first attempt that finds no row does neither. `Test.Integration/SetBasedVersionStampTests.cs` on both providers: a PUT with a task's ETag from before the Category delete's detach answers 412 through the API host (400 before, on the category FK), and the overdue mark, template pointer advance and occurrence insert stamp Version and the timestamps (all 4 failed before) |
| Idempotency-Key header (D-074) | proven | `Test.Endpoints/IdempotencyKeyEndpointTests.cs` (service and CQRS: same key replays one task, comment or checklist item; another key creates another; a body id wins; a blank, 201-character or repeated key is a 400 ProblemDetails; a key on two tasks maps to two comments); `Test.Integration/IdempotencyKeyIntegrationTests.cs` on PostgreSQL and SQL Server (same key, other key, tenant and scope; a staged same-key race returns the winner's id; purge by age; HTTP replay and a raced create that ends in one task row); `AiWorkflowIntegrationTests.KeyedCommentPost_RetriedOn502_AddsOneComment` (the shipped keyed comment node, first response lost as a 502, resent with the same key, one comment). Before the filter was wired, the header cases failed (a second 201, no 400, two comments); `WorkflowDefinitionValidityTests` pins the keyed create and child-add POST nodes, loop-body nodes included; `FlowEngineWorkflowTests` proves three loop iterations send three keys and each 502 resend reuses its iteration's key, and `AiWorkflowIntegrationTests.DecomposerLoopBodyPost_RetriedOn502_CreatesOneSubtaskPerIteration` proves lost 502 responses leave one subtask per iteration through the real API. Review fixes, each failing before on the cases named: keys differing only by case are two keys on SQL Server (binary collation; failed on SQL Server); the task id in another route format is one scope; an empty body id maps the key (400 before); a child add to a missing or foreign task stores no mapping; the purge deletes 2001 rows in at least three statements (one before); Category, Tag and Attachment creates replay by key (6 of 6 failed); the six keyed routes declare the optional header in OpenAPI. `Post_ConcurrentSameKeyComment_OnTheHostsScopedContext_AddsOneComment` runs the filter's raced mapping save and the handler's retry on the host's one scoped context on both providers (it fails with a 500 when the failed mapping row stays tracked); `ConsumerInboxRetentionHandlerTests` pins the job's purge call |
| Gateway identity relay and token acquisition (D-068) | proven | `Test.Endpoints/ForwardedClaimsRelayTests.cs` (trusted app-only token yields only the relayed user, delegated or unlisted callers are not trusted, shipped settings trust nobody, Gateway and Api `ForwardedClaims` settings agree, a trusted app-only caller without a relay header gets 403 on a data route and neither 401 nor 403 on `/health/full`, and 403 there once `ServicePathPrefixes` is cleared); `Test.Unit/Gateway/GatewayAuthModeTests.cs` (every route's cluster relays user claims, and the forged inbound header is replaced through the Gateway's YARP transforms); `Test.Unit/Gateway/GatewayAccessTokenCacheTests.cs` (single-flight, first caller cancelling, faulted acquisition not cached, refresh before expiry). Workflow self-call relay: `Test.Unit/Hosting/SelfCallRelayTests.cs` (sends run inside a node of a real in-memory engine instance, the tenant read from `FlowExecution.Current`; the package `integration` and `fetch` nodes relay the instance tenant, and the package refuses their URLs that leave the client's base address; the instance tenant, subject, name and `TenantMember` alone in a header the Api's shipped section decodes; a node-supplied header replaced; a target off the self-call base address or a node-supplied `Authorization` header fails before a token is acquired; no instance tenant fails before sending; nothing added with the relay off; an allowlist without the tenant claim fails start), `Test.Endpoints/WorkflowRateLimitPartitionTests.cs` (own partition and budget for relay-built workflow principals only), `Test.Unit/Infrastructure/SelfCallRelayShippedOffTests.cs` (shipped settings and every deployment leave it off), and `AiWorkflowIntegrationTests.RelayHeader_ActsForTheRelayedTenantOnly_AndIsRefusedFromAnUntrustedCaller` and `RelayOn_InstanceWithoutATenant_TakesTheErrorEdge_WithTheReasonRecorded` on PostgreSQL and SQL Server through a test-only app token scheme. A live token scheme is deployment-only |
| Tenant rate limiting and edge limits (D-050) | proven | `TenantRateLimitEndpointTests` (tenant tier applied after auth, export counted once); `TenantRateLimitingCompositionTests` (unreachable Redis fails open, no Redis stays in process); `GatewayEdgeRateLimitTests.AddGatewayServices_InvalidEdgeBudget_FailsWhenTheLimiterIsBuilt`; `BicepInfrastructureContractTests.MainBicep_GatewayAppliesExactlyOneForwardedHop` |
| Two-state inbox and outbox settlement (D-026, D-029, D-053) | proven | `Test.Unit/Infrastructure/MessagingConsumerTests.cs` (TaskFlow consumers on the package base); `Test.Integration/InboxStoreTests.cs` and `OutboxClaimTests.cs` on both providers; `Test.Aspire/OutboxMeshTests.cs` (every consumer records the event exactly once); `BrokerTracePropagationTests.OutboxHop_ConsumerContinuesTheRequestTrace_NotTheDrain`; settlement and transports are proven by the `EF.Data.Outbox` and `EF.Messaging` suites |
| Attachment content lifecycle (D-026, D-075, D-033) | proven | `Test.Endpoints/AttachmentEndpointTests.cs`, service and CQRS: a PUT that changes the content type, size or storage URI of an uploaded attachment, or the owner of any attachment, is a 400 and the row is unchanged, while a rename and a metadata-only full replace answer 200; deleting an uploaded attachment stages exactly one `BlobDeleteWork` row for its stored key and one drain through `BlobDeleteWorkerService.DeleteBatchAsync` removes the blob (the rename-then-delete case too), a metadata-only delete stages none; a successful upload leaves no reservation row, a repeated caller id replays without a second blob, another payload under it is a 409, an upload under a metadata-only row's id is a 409 that writes nothing, a reused caller id deletes again with a second staged row, and a failed blob write keeps the reservation; a failed reservation save writes no blob (`Test.Unit/Services/AttachmentServiceTests.cs`, both styles). `Test.Unit/Domain/AttachmentTests.cs` (the `Attachment.Update` rule). `Test.Integration/AttachmentUploadReservationTests.cs` on PostgreSQL and SQL Server, both styles: an upload whose insert loses a same-id race replays the winner, its reservation is not claimable by the package claim inside the grace period, and after it one drain deletes the orphaned blob and settles the row; a reservation the worker claims while the upload is in flight makes the lease-guarded release remove no row, so the upload answers save-failed (not 412) with no attachment row, and the worker's completion deletes the blob. `Test.Integration/WildcardDeleteLandedCommitTests.cs` (one staged row after a landed, re-sent delete; a delete failure with the row stored keeps its `DbUpdateException`) |
| Scheduler cron seeding and health (D-009) | proven | `SchedulerCronRegistrationTests`; `Test.Integration/SchedulerCronSeedingTests.HostStart_SeedsEveryDeclaredCronJob_AndARestartAddsNone` (plus a due ticker run through `ScheduledJobRunner`); `SchedulerHealthCheckTests` (package check with the shipped threshold) |
| Compliance-check start path (D-075, D-009, D-068) | proven | The Scheduler's daily `ComplianceCheck` job (06:10 UTC) starts `compliance-check` with `StartRequest.TenantId` set for each tenant with an open task tagged `compliance` due within `Scheduling:Compliance:WindowDays` that the workflow's API calls (the task search, the attachment search and the comment posts) can act for: every such tenant with the self-call relay configured, the scaffold tenant alone in the shipped Scaffold mode, where the calls authenticate as the scaffold principal; every other qualifying tenant is logged as not started (follow-up 9). `ComplianceCheckHandlerTests` (Scaffold mode: scaffold tenant only, its tenant, params and window; one Warning per other tenant and a successful run; relay mode: every qualifying tenant with its own tenant; key stable for the UTC day; a same-day re-run resolves through the engine's `IdempotencyKey` to the day's instance; a failed start is rethrown after every tenant is handled; cancellation stops the run; `WindowDays` 1 to 365 validated on start); `AiWorkflowIntegrationTests.ComplianceCheckJob_StartsTheScaffoldTenantOnly_AndItsInstanceScansItsDueTasks` (another qualifying tenant gets no instance; the scaffold tenant's instance scans its two due tasks, one child each, and reads the evidence; a same-day re-run resolves to the day's instance); `AiWorkflowIntegrationTests.ComplianceCheckJob_StartsOneInstancePerQualifyingTenant_CarryingThatTenant` (relay mode on PostgreSQL and SQL Server: both tenants start, tenant B's instance scans only B's two due tasks, reads B's evidence and reminds B's task; a same-day re-run resolves to each tenant's instance); `SchedulerJobIntegrationTests.ComplianceTenantStream_PagesOfOne_YieldEachQualifyingTenantOnce`; `SchedulerCronRegistrationTests` and `SchedulerCronSeedingTests` (the cron row); `Test.Aspire/ComplianceCheckSchedulerSmokeTests` (NonAzure graph with the Scheduler, 2026-10-06: a due `ComplianceCheck` ticker starts a new scaffold-tenant instance whose `compliance-check-item` child reads the uploaded text attachment and ends on `n-done`; 1 passed). Self-call address (`FlowEngine:TaskFlowApiBaseUrl`) for the Scheduler, the Api and Functions: `DeploymentWorkflowContractTests`, `AppHostLaneTopologyTests`, `BicepInfrastructureContractTests.MainBicep_EveryWorkflowHost_ReceivesTheApiAddressForSelfCalls`; the Azure values are deployment-only. The same-day dedupe is the engine's `IdempotencyKey` alone: the re-run cases pass on PostgreSQL and SQL Server after other instances are saved (2026-10-09). Admin starts: `AiWorkflowIntegrationTests.AdminStart_CarriesTheRequestedTenant_AndWithoutOneIsRefusedTheEvidence` (no `TenantId` refused the evidence; `TenantId` = the scaffold tenant reads it and reminds the task; another `TenantId` from the scaffold principal starts in that tenant). `ComplianceCheckSchedulerSmokeTests` (2026-10-09, `TASKFLOW_ASPIRE_SCHEDULER_AVAILABLE=true`): 1 passed. |
| Scheduler transaction retry (D-009) | proven | `Test.Integration/SchedulerTransactionRetryTests.cs` on PostgreSQL and SQL Server: a `DbTransactionInterceptor` throws one provider-transient exception (PostgresException 40001; `TimeoutException`, which the SQL Server strategy retries) before or after the first commit of each job. All three jobs complete with one outbox or blob-delete row per message id; after a lost commit the count is reported once, after a landed commit the retry is a no-op and reports 0. A competing overdue replica that marks and announces two of three tasks inside the run's window leaves the run announcing and reporting only the third, and a recurrence pointer re-seeded over generated occurrences announces only the new ones; both failed on `PK_OutboxMessage` on both providers before each job staged only the rows its own guarded write changed. An overlapping stale-cleanup run that removes two of three stale tasks and queues their blob work just before the run's first write leaves the run queueing and reporting only the third; it failed on `PK_BlobDeleteWork` on both providers before blob work followed the guarded per-task delete. Before the per-attempt tracker clear the three before-commit cases failed on both providers ("already being tracked" on the re-staged `OutboxMessage` or `BlobDeleteWork`) |
| Strict config and id validation (D-069) | proven | `EF.Common` `StrictEnum` and `EF.Common.Contracts.UuidV7` (package suites); `TaskFlowDbProviderSelectorTests.PoolerModeSelector_NumericOrCombinedValue_Throws`; Endpoint cases reject a non-v7 caller id with 400 |
| Audit masking of secure columns (D-023) | proven | `AuditMaskingTests.Given_SecureTaskItemValues_When_CreatedAndUpdated_Then_AuditPayloadsCarryNoPlaintext` |
| Uno WASM host contract (D-070) | proven | `Test.UI/WasmHost/WasmHostHttpContractTests.cs` (encoding by Accept-Encoding quality, immutable fingerprinted assets, no-cache index, 404 for missing assets, SPA fallback, `/app-config.json`, the publish-rewritten `uno-config.js` served from disk); nginx static contract in `DeploymentWorkflowContractTests` |
| Test prerequisites and default-lane test hosts (D-071) | proven | `Test.Unit` `TestPrerequisiteContractTests`; `Test.Endpoints/EndpointHostLaneTests.cs` (endpoint host boots the NonAzure lane container-free) |
| NonAzure default lane (D-060) | proven | `HostingLaneContractTests.Resolve_Unset_ReturnsExactNonAzureProfile`; `AppHostLaneTopologyTests.FullLane_UnsetLaneGraph_IsNonAzureTopology`; `BicepInfrastructureContractTests.AzureDeployment_EveryHostSetsTheAzureLaneExplicitly`; AI provider switch in `AppHostLaneTopologyTests.AzureFoundry_*` |
| Request timeouts, shutdown drain, runtime evidence (D-064) | proven (wiring); deployment-only (live drain) | `Test.Endpoints/RequestTimeoutEndpointTests.cs` and `Test.Unit/Gateway/GatewayRequestTimeoutTests.cs` (default policies, streaming opt-outs, YARP `TimeoutPolicy: Disable`); `Test.Unit/Hosting/ServiceDefaultsScaleTests.cs` (readiness unhealthy for the drain, budget validation); `Test.Unit/Infrastructure/BicepInfrastructureContractTests.cs` and `DeploymentWorkflowContractTests.cs` (Container Apps drain, Compose budget) |
| Head trace sampling (D-065) | proven | `Test.Unit/Hosting/ServiceDefaultsScaleTests.cs` (ratio 0 drops and ratio 1 records a root span; out-of-range fails startup) |
| NonAzure deployment observability (D-061) | proven (wiring and contracts); deployment-only (live runtime) | `Test.Unit/Hosting/OpenTelemetryMetricsRegistrationTests.cs` proves the runtime metrics switch; `Test.Endpoints/GlobalExceptionHandlerTests.cs` proves server `requestId` plus W3C `traceId`/`spanId` exception correlation; endpoint contracts prove the same correlation fields on typed errors; `Test.Unit/Infrastructure/DeploymentWorkflowContractTests.cs` proves OpenObserve isolation, credentials, OTLP, retention, and deploy gates. `deploy/compose/docker-compose.yml`, `docker-compose.override.local.yml`, and `.github/workflows/deploy-vps.yml` wire deployed OpenObserve OSS with metrics export disabled by default; local Aspire retains its Dashboard defaults. Compose shapes and workflow YAML validated, but no live OpenObserve container or deployment ran. |

The declared flags and matrix must agree with `.scaffold/resource-implementation.yaml`. Proof paths are validated against the scaffold-owned [TaskFlow proof map](https://github.com/efreeman518/scaffold-ai/blob/main/support/taskflow-proof-map.md).

## Phase Completion

Phases 1 through 5e and the FlowEngine extension are complete. Root `HANDOFF.md` records `workflowStatus: complete`, `currentPhase: 5`, and `currentSubPhase: complete`. Later work is ordinary maintenance on that completed baseline, not a phase re-open.

## Infrastructure as Code

`infra/` contains the Bicep deployment baseline: top-level `main.bicep`, resource modules, deployment scripts, and rollback contracts. `deploy/compose/` contains the strict NonAzure Docker Compose baseline (Caddy, PgBouncer, SeaweedFS, images.env, VPS runbook) - see `deploy/compose/README.md`.

Deployment plan: [`.azure/deployment-plan.md`](../.azure/deployment-plan.md).

Validate locally with `az bicep build --file infra/main.bicep` and `docker compose -f deploy/compose/docker-compose.yml config -q`.

## Outstanding Follow-Ups

1. Azure Aspire full-graph acceptance (Functions on) was last observed blocked on 2026-09-16 by Aspire's SQL child-database probe running before `ResourceReadyEvent` creates the database; it was not rerun on 2026-09-29, when the Azure core-lane mesh passed. Aspire 13.5.3 and 13.5.4 behaved identically; the Azure full graph has not run on Aspire 13.6.1. Remove this blocker only after an upstream release changes that ordering and the exact Azure/Cosmos CI filter passes all 10 selected tests without skips or inconclusive results.
2. Five live Azure AI Foundry tests require an externally provisioned endpoint, deployment, and credential. They were not run on 2026-09-29.
3. The 5,000 RPS load gate is deployment-only by design. No VPS or Azure deployment ran.
4. Machine-level tooling updates require actions outside this repository: finish the .NET workload manifest update, run the administrator Node.js MSI, accept Android licenses, and replace the preview Functions CLI when a working current bootstrap is available.
5. Browser WASM trimming remains disabled for Release because upstream Uno dependencies emit `IL2104` under warnings-as-errors. Remove the workaround when those packages become trim-clean; current cold-start and full-browser evidence passes on both lanes (2026-09-29).
6. iOS compile proof is current, but runnable iOS Appium acceptance requires a supported macOS/Xcode host.
7. The scaffold-ai TaskFlow proof map and feature sentinels still name the app files and calls the EF.Packages 2.0 adoption replaced (35 `validate-reference.py` issues); the scaffold-ai side owns that update.
8. Deployment configuration: in the shipped Scaffold mode workflow self-calls run as the scaffold principal and count against the scaffold tenant's `standard` tier (100 requests per 60 s), the budget the UI uses; a compliance-check run makes about two calls per due task (one page of at most 50 tasks), each `taskflow-api` node retries a 429 up to its `retryPolicy.maxAttempts` (3, exponential) honoring `Retry-After`, and a node that still gets 429 takes its Error edge. A live-identity deployment with the self-call relay lists the workflow hosts' client ids in the Api's `RateLimiting:Workflow:CallerIds`, so relayed self-calls spend each tenant's own `workflow` budget (`RateLimiting:Tenants:Budgets:workflow`, 300 per 60 s) instead of its tier (D-068).
9. Deployment configuration: the workflow self-call identity (D-068). In the shipped Scaffold mode the `ComplianceCheck` job starts the scaffold tenant only and logs every other tenant with a due compliance task as not started. A live-identity deployment covers every tenant by setting, on the Api, its token scheme (TaskFlow ships none) and the workflow hosts' app-only client ids in `ForwardedClaims:TrustedCallerIds` and `RateLimiting:Workflow:CallerIds`, and on every workflow host (Api, Scheduler, Functions) `FlowEngine:SelfCall:TokenScope` and the identity the token is issued to (`ManagedIdentityClientId`), plus, on Functions (its `appsettings.json` is not in its build output), the `ForwardedClaims` header name and claim types; the relay path is proven in Test.Integration with a test-only token scheme, and no live Entra deployment ran.
10. Azurite 3.37.0, the latest release, accepts storage service versions up to 2026-06-06, and Azure.Storage.Blobs 12.30 (the floor of EF.Storage and EF.AspNetCore.DataProtection 2.0.130) sends 2026-10-06, so the AppHost emulator and the Test.Integration fixture set `AZURITE_SKIP_API_VERSION_CHECK=true` (`ContainerImages.AzuriteSkipApiVersionCheckVariable`). Remove it when an Azurite release supports the SDK's service version and the Azure lane passes without it.
11. Test.Integration runs within EF Core's limit of twenty internal service providers per process with little margin: `ColumnEncryptionOptionsExtension` (EF.Data.Encryption 2.0.130) keys the provider on the encryptor instance, so every test host adds one per encrypted context, and two more hosts made later tests throw `ManyServiceProvidersCreatedWarning` (2026-10-09). The admin start cases share one host; the package owner keys the provider on encryptor equivalence.
