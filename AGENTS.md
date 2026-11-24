# AGENTS - TaskFlow Reference App

TaskFlow is the compiled proof for the [scaffold-ai](https://github.com/efreeman518/scaffold-ai) instruction payload. Scaffolding is complete (Phases 1-5e); sessions here are maintenance: keep the app building, tests green, and the proof surface current. Claude Code loads this file through `CLAUDE.md`.

## Rules

- The scaffold payload is deliberately NOT installed here (no `.instructions/`). Do not install it; scaffold sessions in other repos consult this app as verified call-site source.
- Instruction-set problems found here go to `.scaffold/INSTRUCTION-GAPS.md`, one line each. Never fix instruction text in this repo; scaffold-ai owns it.
- `.scaffold/` artifacts (domain-specification.yaml, UBIQUITOUS-LANGUAGE.md, DESIGN-DECISIONS.md) are binding. A new term, entity, or design decision updates the artifact before the code.
- A change that moves build/test counts or vulnerability state refreshes `.scaffold/REFERENCE-STATUS.md` (current truth) in the same commit. Root `HANDOFF.md` holds only the `workflowStatus: complete` sentinel that scaffold-ai's `validate-reference.py` checks.

## Layout

| Layer | Path |
|---|---|
| Domain | `src/Domain/` |
| Application | `src/Application/` |
| Infrastructure | `src/Infrastructure/` (EF Core, AI, Storage, Repositories, `TaskFlow.Infrastructure.Caching` - EF.Cache/FusionCache composition, `TaskFlow.Infrastructure.Messaging.RabbitMq` - adapter over the published `EF.Messaging.RabbitMq`, `TaskFlow.Infrastructure.Data.Migrations.{SqlServer,PostgreSql}` - per-provider migration assemblies) |
| Hosts | `src/Host/` (Api, Gateway, Scheduler, Functions, DatabaseMigrator, Bootstrapper, Aspire, Uno WASM) |
| UI clients | `src/UI/TaskFlow.ApiClient` (Refitter-generated shared client for Blazor) alongside Blazor, React, Uno |
| Tests | `tests/` (14 projects; verified counts in REFERENCE-STATUS.md) |

## Build and test

```powershell
dotnet build TaskFlow.slnx                                     # 0 warnings expected
dotnet build src/UI/TaskFlow.Uno/TaskFlow.Uno.csproj           # Uno builds separately (Uno.Sdk)
dotnet test --project tests/Test.Unit/Test.Unit.csproj          # full unit project, including untagged provider/contract tests
dotnet test --solution TaskFlow.slnx --filter "TestCategory=Unit"   # category-filtered fast lane
dotnet test --solution TaskFlow.slnx --filter "TestCategory=Architecture|TestCategory=Endpoint"
dotnet test --solution TaskFlow.slnx --no-build --max-parallel-test-modules 1   # unfiltered serial acceptance; full-stack projects are resource-heavy
dotnet run --project src/Host/Aspire/AppHost                   # NonAzure stack (default): PostgreSQL JSONB + RabbitMQ + SeaweedFS + Redis
$env:TASKFLOW_LANE = "Azure"; dotnet run --project src/Host/Aspire/AppHost                 # local Azure emulator stack (opt-in)
$env:TASKFLOW_READMODEL_PROVIDER = "MongoDb"; dotnet run --project src/Host/Aspire/AppHost # NonAzure document database alternative
```

CI runs the full Test.Unit project under a sub-minute timeout with blame-hang diagnostics. Container, Aspire, browser, and full-stack lanes are `workflow_dispatch`-only.

## Provider selection

Providers switch by config only, no code change. `TASKFLOW_LANE=Azure|NonAzure` (default NonAzure, D-060) owns the core topology:

- Azure: SqlServer, ServiceBus, AzureBlob/Azurite, Cosmos, AzureTable, Blob Data Protection, Azure-only Functions.
- NonAzure: PostgreSql, RabbitMq, S3/SeaweedFS, Relational audit, Redis Data Protection, PostgreSqlJsonb read model (optional MongoDb).
- Both lanes: Redis, DatabaseMigrator, Api, Gateway, Scheduler (EF.Data.Scheduling cron jobs), Blazor, React, Uno WASM.
- Lane-owned provider conflicts fail fast; NonAzure also rejects non-empty Azure App Configuration and Key Vault settings.

| Env var | Values | Notes |
|---|---|---|
| `TASKFLOW_DB_PROVIDER` | SqlServer, PostgreSql | lane default |
| `TASKFLOW_MESSAGING_PROVIDER` | ServiceBus, RabbitMq | lane default |
| `TASKFLOW_STORAGE_PROVIDER` | AzureBlob, S3 | D-037 |
| `TASKFLOW_READMODEL_PROVIDER` | Cosmos, PostgreSqlJsonb, MongoDb | D-038 |
| `TASKFLOW_AUDIT_PROVIDER` | AzureTable, Relational | D-039 |
| `TASKFLOW_SEARCH_PROVIDER` | AzureAiSearch, PgVector, Sql | local default Sql; PgVector requires NonAzure PostgreSql (D-040) |
| `TASKFLOW_AI_PROVIDER` | AzureInference, OpenAICompatible, None | local default None (D-041) |
| `TASKFLOW_DATAPROTECTION_PERSISTENCE` | AzureBlob, Redis, None | strict lane defaults AzureBlob / Redis (D-043) |

- `Database:PostgreSql:PoolerMode=None|Transaction` (config only, default None) appends PgBouncer transaction-pooling connection-string flags (D-045).
- Health probes (D-049): `/healthz/live` (self only, restart-worthy), `/healthz/ready` (database, outbox, scheduler, broker on consumer hosts), `/healthz` (aggregate, for humans and Compose healthchecks). `/readyz` is removed.

## Containers

- Container-backed tests need a Docker-compatible runtime: Docker Desktop, headless Docker Engine, or Podman.
- Maintainer machine (verified 2026-09-11): Podman with a Docker-compatible context, WSL `networkingMode=mirrored`, Podman machine `UserModeNetworking=false`. Published `127.0.0.1` ports work, so Testcontainers, Aspire/DCP, and Playwright need no `TESTCONTAINERS_HOST_OVERRIDE`.
- Under legacy Podman WSL NAT, only component Testcontainers lanes can use a run-scoped `TESTCONTAINERS_HOST_OVERRIDE=<podman machine ip>`. Aspire/DCP and full-stack Playwright bind container endpoints to loopback and need mirrored networking or Docker.

## Pointers

- Current verified build/test/vulnerability state: `.scaffold/REFERENCE-STATUS.md`
- Detailed design: `docs/tech-design.html` (maintenance rules: `docs/TECH-DESIGN-MAINTENANCE.md`)
- Deployment: `infra/` (Bicep), `.azure/deployment-plan.md`
- Client regeneration (Refitter/openapi-typescript): `docs/plans/client-generation.md`
