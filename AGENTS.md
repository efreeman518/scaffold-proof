# AGENTS - TaskFlow Reference App (maintainer sessions)

TaskFlow is the compiled proof for the [scaffold-ai](https://github.com/efreeman518/scaffold-ai) instruction payload. Scaffolding is complete (Phases 1-5e); sessions here are maintenance: keep the app building, tests green, and the proof surface current.

This file is the single source of maintainer-session instructions: CLI agents and GitHub Copilot agent surfaces (including VS Code) read root `AGENTS.md` natively; Claude Code loads it through the `@AGENTS.md` import in `CLAUDE.md`.

## Rules

- The scaffold payload is deliberately NOT installed in this repo (no `.instructions/`). Do not install it here; the app is consulted BY scaffold sessions in other repos as verified call-site source.
- Instruction-set problems found while working here go to `.scaffold/INSTRUCTION-GAPS.md` (one line each). Never fix instruction text in this repo - the source repo owns it.
- `.scaffold/` artifacts (domain-specification.yaml, UBIQUITOUS-LANGUAGE.md, DESIGN-DECISIONS.md) are binding source of truth. New term, entity, or design decision -> update the artifact before the code.
- When a change moves build/test counts or vulnerability state, refresh `.scaffold/REFERENCE-STATUS.md` in the same commit. `HANDOFF.md` is historical; REFERENCE-STATUS.md is current truth.

## Layout

| Layer | Path |
|---|---|
| Domain | `src/Domain/` |
| Application | `src/Application/` |
| Infrastructure | `src/Infrastructure/` (EF Core, AI, Storage, Repositories, `TaskFlow.Infrastructure.Caching` - EF.Cache/FusionCache composition, `TaskFlow.Infrastructure.Messaging.RabbitMq` - RabbitMQ transport adapter over the published `EF.Messaging.RabbitMq`, `TaskFlow.Infrastructure.Data.Migrations.{SqlServer,PostgreSql}` - per-provider migration assemblies) |
| Hosts | `src/Host/` (Api, Gateway, Scheduler, Functions, DatabaseMigrator, Bootstrapper, Aspire, Uno WASM) |
| UI clients | `src/UI/TaskFlow.ApiClient` (Refitter-generated shared client for Blazor) alongside Blazor, React, Uno |
| Tests | `tests/` (15 projects; see REFERENCE-STATUS.md for verified counts) |

## Build and test

```powershell
dotnet build TaskFlow.slnx                                     # 0 warnings expected
dotnet build src/UI/TaskFlow.Uno/TaskFlow.Uno.csproj           # Uno builds separately (Uno.Sdk)
dotnet test tests/Test.Unit/Test.Unit.csproj                    # full unit project, including untagged provider/contract tests
dotnet test TaskFlow.slnx --filter "TestCategory=Unit"         # category-filtered solution fast lane
dotnet test TaskFlow.slnx --filter "TestCategory=Architecture|TestCategory=Endpoint"
dotnet test TaskFlow.slnx --no-build -m:1                     # unfiltered serial acceptance; full-stack projects are resource-heavy
# Dual EF Core provider: TASKFLOW_DB_PROVIDER=SqlServer|PostgreSql selects the runtime provider (default SqlServer)
# TASKFLOW_TEST_DB_PROVIDER=SqlServer|PostgreSql is a deprecated test-only alias for one release; new orchestration uses TASKFLOW_LANE
# TASKFLOW_MESSAGING_PROVIDER=ServiceBus|RabbitMq selects the messaging transport (default ServiceBus); RabbitMq needs no code change, only config
# TASKFLOW_LANE=Azure|NonAzure owns the core provider topology (default Azure); Portable is a deprecated NonAzure alias for one release - D-060
# Azure: SqlServer, ServiceBus, AzureBlob/Azurite, Cosmos, AzureTable, Blob Data Protection, and Azure-only Functions
# NonAzure: PostgreSql, RabbitMq, S3/SeaweedFS, Relational audit, Redis Data Protection, PostgreSqlJsonb read model by default, optional MongoDb
# Lane-owned provider conflicts fail fast; NonAzure also rejects non-empty Azure App Configuration and Key Vault service settings
# TASKFLOW_STORAGE_PROVIDER=AzureBlob|S3 selects the object-storage backend within its compatible lane - D-037/D-060
# TASKFLOW_READMODEL_PROVIDER=Cosmos|PostgreSqlJsonb|MongoDb selects the read-model backend; Relational is a deprecated PostgreSqlJsonb alias - D-038/D-060
# TASKFLOW_AUDIT_PROVIDER=AzureTable|Relational selects the lane-compatible audit sink - D-039/D-060
# TASKFLOW_SEARCH_PROVIDER=AzureAiSearch|PgVector|Sql selects search; local default Sql, PgVector requires NonAzure PostgreSql - D-040/D-060
# TASKFLOW_AI_PROVIDER=AzureInference|OpenAICompatible|FoundryLocal|None selects the lane-compatible LLM client; local default None - D-041/D-060
# TASKFLOW_DATAPROTECTION_PERSISTENCE=AzureBlob|Redis|None selects persistence; strict lane defaults are AzureBlob and Redis - D-043/D-060
# Database:PostgreSql:PoolerMode=None|Transaction (config only, no env var) appends the PgBouncer transaction-pooling connection-string flags (default None) - D-045
# Health probes (D-049): /healthz/live (self only, restart-worthy), /healthz/ready (database, outbox, scheduler, broker on consumer hosts), /healthz (aggregate, humans + Compose healthchecks); /readyz removed
# Redis, DatabaseMigrator, API, Gateway, Scheduler with embedded TickerQ, Blazor, React, and Uno WASM run in both lanes
# CI runs the full Test.Unit project under a sub-minute timeout with blame-hang diagnostics; all container, Aspire, browser, and full-stack lanes are workflow_dispatch-only
# Container-backed tests need a Docker-compatible runtime; Docker Desktop, headless Docker Engine, and Podman are supported.
# Podman on Windows/WSL2 uses mirrored networking on this machine, so published localhost ports work without TESTCONTAINERS_HOST_OVERRIDE.
# Under legacy WSL NAT, only the component Testcontainers lanes can use a run-scoped TESTCONTAINERS_HOST_OVERRIDE=<podman machine ip>;
# Aspire/DCP and full-stack Playwright require localhost forwarding and therefore need mirrored WSL networking or Docker.
dotnet run --project src/Host/Aspire/AppHost                   # full local Azure stack (default)
$env:TASKFLOW_LANE = "NonAzure"; dotnet run --project src/Host/Aspire/AppHost   # PostgreSQL JSONB + RabbitMQ + SeaweedFS + Redis, zero Azure
$env:TASKFLOW_READMODEL_PROVIDER = "MongoDb"; dotnet run --project src/Host/Aspire/AppHost # explicit NonAzure document database alternative
```

## Pointers

- Current verified build/test/vulnerability state: `.scaffold/REFERENCE-STATUS.md`
- Detailed design: `docs/tech-design.html` (maintenance rules: `docs/TECH-DESIGN-MAINTENANCE.md`)
- Deployment: `infra/` (Bicep), `.azure/deployment-plan.md`
- Client regeneration (Refitter/openapi-typescript): `docs/plans/client-generation.md`

## Environment facts (this machine, verified 2026-09-11)

- On this machine only, the active container runtime is Podman with a Docker-compatible context. `%UserProfile%\.wslconfig` sets `[wsl2] networkingMode=mirrored`, and the Podman machine keeps `UserModeNetworking=false`; a Windows request to a container published on `127.0.0.1` succeeds. Testcontainers, Aspire/DCP, and Playwright therefore use localhost without `TESTCONTAINERS_HOST_OVERRIDE` here.
- Other machines may use Docker Desktop or a headless Docker Engine; no UI-specific behavior is required. Under legacy Podman WSL NAT, component Testcontainers lanes can use a run-scoped `TESTCONTAINERS_HOST_OVERRIDE`, but Aspire/DCP and full-stack Playwright cannot because their container endpoints are loopback-bound.
