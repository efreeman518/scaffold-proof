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
| Infrastructure | `src/Infrastructure/` (EF Core, AI, Storage, Repositories, `TaskFlow.Infrastructure.Caching` - FusionCache, `TaskFlow.Infrastructure.Messaging.RabbitMq` - RabbitMQ transport adapter, `TaskFlow.Infrastructure.Data.Migrations.{SqlServer,PostgreSql}` - per-provider migration assemblies) |
| Packages | `src/Packages/EF.Messaging.RabbitMq` (portable RabbitMQ wrapper: connection multiplexer, topology, consumer host - no TaskFlow dependencies; candidate for the EF.* package feed) |
| Hosts | `src/Host/` (Api, Gateway, Scheduler, Functions, DatabaseMigrator, Bootstrapper, Aspire, Uno WASM) |
| UI clients | `src/UI/TaskFlow.ApiClient` (Refitter-generated shared client for Blazor) alongside Blazor, React, Uno |
| Tests | `tests/` (16 projects incl. `EF.Messaging.RabbitMq.Tests`; see REFERENCE-STATUS.md for verified counts) |

## Build and test

```powershell
dotnet build TaskFlow.slnx                                     # 0 warnings expected
dotnet build src/UI/TaskFlow.Uno/TaskFlow.Uno.csproj           # Uno builds separately (Uno.Sdk)
dotnet test TaskFlow.slnx --filter "TestCategory=Unit"         # fast lane
dotnet test TaskFlow.slnx --filter "TestCategory=Architecture|TestCategory=Endpoint"
dotnet test TaskFlow.slnx --no-build -m:1                     # unfiltered serial acceptance; full-stack projects are resource-heavy
# Dual EF Core provider: TASKFLOW_DB_PROVIDER=SqlServer|PostgreSql selects the runtime provider (default SqlServer)
# TASKFLOW_TEST_DB_PROVIDER=SqlServer|PostgreSql selects the container-backed test lane (default SqlServer); rerun Integration/E2E/FlowEngine-integration with both values
# TASKFLOW_MESSAGING_PROVIDER=ServiceBus|RabbitMq selects the messaging transport (default ServiceBus); RabbitMq needs no code change, only config
# TASKFLOW_LANE=Azure|Portable seeds the DEFAULT of every switch below (each switch's own env/config still wins; default Azure) - D-035
# TASKFLOW_STORAGE_PROVIDER=AzureBlob|S3 selects the object-storage backend (default AzureBlob) - D-037
# TASKFLOW_READMODEL_PROVIDER=Cosmos|Relational selects the read-model backend (default Cosmos) - D-038
# TASKFLOW_AUDIT_PROVIDER=AzureTable|Relational selects the audit-sink backend (default AzureTable) - D-039
# TASKFLOW_SEARCH_PROVIDER=AzureAiSearch|PgVector|Sql selects the search backend (default: Portable lane -> Sql, Azure lane -> AzureAiSearch when AiServices:UseSearch else Sql; PgVector requires Database:Provider=PostgreSql, fails fast otherwise) - D-040
# TASKFLOW_AI_PROVIDER=AzureInference|OpenAICompatible|FoundryLocal|None selects the LLM client (default derived from ConnectionStrings:chat) - D-041
# TASKFLOW_DATAPROTECTION_PERSISTENCE=AzureBlob|Redis|None selects the Data Protection key-ring persistence (default derived from DataProtectionKeysFileUrl) - D-043
# Database:PostgreSql:PoolerMode=None|Transaction (config only, no env var) appends the PgBouncer transaction-pooling connection-string flags (default None) - D-045
# Health probes (D-049): /healthz/live (self only, restart-worthy), /healthz/ready (database, outbox, scheduler, broker on consumer hosts), /healthz (aggregate, humans + Compose healthchecks); /readyz removed
# E2E/Integration/RabbitMq-container tests need a container runtime. This machine runs Podman, and Podman WSL2 does not forward container ports to localhost:
#   podman machine ssh -- ip route get 1.1.1.1             # prints "1.1.1.1 via <gw> dev eth0 src <ip>"; use the src address
#   TESTCONTAINERS_HOST_OVERRIDE=<podman machine ip>       # run-scoped only, changes on reboot, never commit
dotnet run --project src/Host/Aspire/AppHost                   # full local stack (Azure lane, default)
$env:TASKFLOW_LANE = "Portable"; dotnet run --project src/Host/Aspire/AppHost   # Portable topology locally (Postgres+RabbitMQ+MinIO, no Azure emulators); deploy/compose/README.md is the VPS Docker Compose runbook
```

## Pointers

- Current verified build/test/vulnerability state: `.scaffold/REFERENCE-STATUS.md`
- Detailed design: `docs/tech-design.html` (maintenance rules: `docs/TECH-DESIGN-MAINTENANCE.md`)
- Deployment: `infra/` (Bicep), `.azure/deployment-plan.md`
- Client regeneration (Refitter/openapi-typescript): `docs/plans/client-generation.md`

## Environment facts (this machine, verified 2026-09-04)

- Container runtime is Podman with a Docker-compatible context. Testcontainers-backed lanes (Test.Integration, Test.E2E, Test.Integration.FlowEngine, EF.Messaging.RabbitMq.Tests integration) work with a run-scoped `TESTCONTAINERS_HOST_OVERRIDE` set to the current podman machine IP - Podman WSL2 does not forward container ports to `localhost` from Windows.
- Aspire/DCP does not work on this machine: it binds published container ports to `127.0.0.1` inside the Podman WSL VM (`podman port` shows `127.0.0.1:<port>`), unreachable from the Windows host, independent of the Testcontainers override above. `Test.Aspire` and the full-stack `Test.PlaywrightUI` lanes cannot run here until either Docker Desktop replaces Podman or the podman machine networking is reconfigured for port forwarding. `.scaffold/REFERENCE-STATUS.md` records this as the reason those lanes are unverified rather than failing.
