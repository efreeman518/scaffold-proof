# REFERENCE-STATUS - TaskFlow

Canonical current evidence for the TaskFlow reference application. Historical phase narrative belongs in Git history; root `HANDOFF.md` contains only terminal routing state.

> Update this file only from observed results. TaskFlow CI records the scaffold checkout commit used for cross-repository validation so failures remain diagnosable without creating a compatibility pin.

## Build Status

| Field | Value |
|---|---|
| Last verified | 2026-08-18 |
| Solution | `TaskFlow.slnx` |
| Target framework | .NET 10 |
| Solution projects | 44 |
| Errors | 0 |
| Warnings | 0 |

`src/UI/TaskFlow.Uno/TaskFlow.Uno.csproj` builds separately because the Uno SDK requires explicit invocation.

## Test Status

| Project | Category filter | Verified count | Notes |
|---|---|---:|---|
| Test.Unit | `TestCategory=Unit` | 224 | Includes scaffold auth, deployment and Bicep contracts, application-style switching, AI contracts, middleware, caching, FlowEngine, functions, scheduler, repositories, mappers, and services |
| Test.Architecture | `TestCategory=Architecture` | 22 | Layering, naming, generated-code, endpoint-route, and project coverage checks |
| Test.Endpoints | `TestCategory=Endpoint` | 43 | In-memory WebApplicationFactory coverage for service and CQRS styles |
| Test.E2E | `TestCategory=E2E` | 7 | SQL Testcontainers multi-endpoint workflows for both application styles |
| Test.Integration | `TestCategory=Integration` | 28 | Real SQL and Azurite component coverage |
| Test.Integration.FlowEngine | `TestCategory=Integration` | 16 | Workflow JSON validity and engine integration contracts |
| Test.Aspire | `Aspire`, `Foundry`, `Integration`, `LiveAI` | 20 | Shared Aspire mesh; five Azure Foundry cases are inconclusive without deployed configuration |
| Test.FoundryLocal | `FoundryLocal`, `LiveAI` | 3 | Dedicated live local-model lane; intentionally outside serial acceptance |
| Test.PlaywrightUI | `PlaywrightUI`, `WasmUI`, `Unit` | 9 | Blazor, React, and published Release Uno browser and static-host contracts |
| Test.UI | `UI`, `Presentation` | 62 | Headless UI and presentation contracts, including source-generated JSON coverage |
| Test.Mobile | `MobileUI` | 3 | Dedicated Android emulator and Appium runner; gated outside serial acceptance |
| Test.Load | `TestCategory=Load` | 2 | NBomber; ignored by default and run manually |
| Test.Mutation | n/a | 33 | Mutation-target contract tests |
| Test.Benchmarks | n/a | - | BenchmarkDotNet console runner; build-verified |

Current automated evidence: the solution build passed across 44 projects with zero warnings or errors, and the separate Uno build passed across three projects with zero warnings or errors. Unfiltered serial acceptance passed with 462 passed, zero failed, and 10 skipped: five Azure Foundry-gated, two load ignored by default, and three mobile gated to the dedicated runner. Dedicated Foundry Local passed 3/3 separately. The dedicated mobile runner passed 3/3 separately. Deployment Dockerfiles use non-root chiseled runtime stages; SDK images remain build-stage only.

Published Release Uno cold-start and normal browser projects pass from empty browser state without refresh, retry, sleep, or exception suppression. Browser WASM Release temporarily sets `PublishTrimmed=false` because the current Navigation, Toolkit, and WinUI package set emits upstream `IL2104` under warnings-as-errors. Removal condition: those packages become trim-clean. Validation gate: rerun clean Release publish plus the Uno cold-start and normal Playwright projects before removing the workaround.

## Vulnerability Status

Run `dotnet list package --vulnerable --include-transitive` and capture findings here. Severity policy: [scaffold execution gates](https://github.com/efreeman518/AI-Instructions-Scaffold/blob/main/support/execution-gates.md#vulnerability-audit).

Last audit used `dotnet list TaskFlow.slnx package --vulnerable --include-transitive --no-restore` and reported no vulnerable packages or package vulnerability warnings.

| Package | Severity | Direct/Transitive | Advisory | Notes |
|---|---|---|---|---|
| _None_ | - | - | - | Full solution audit reported no vulnerable packages |

## Capability Coverage

Status meanings:

- `proven`: implemented and covered by executable build, test, or smoke evidence.
- `deployment-only`: generated or wired, but live acceptance requires deployed external resources or identity.
- `documented-only`: example or opt-in documentation exists without active runtime wiring.
- `not enabled`: intentionally absent from the TaskFlow configuration.

| Capability | Status | Evidence boundary |
|---|---|---|
| Service and CQRS application-style switch | proven | Shared Endpoint and SQL E2E suites run both styles; `ApplicationStyleResolver` owns selection |
| SQL persistence, migrations, stable paging | proven | Unit, Integration, E2E, and migrator contracts |
| Aspire, Gateway, Scheduler, Functions | proven | Build, topology, unit, endpoint, and shared-mesh coverage |
| Uno, Blazor, React | proven | Build, Test.UI, Playwright, and dedicated mobile evidence |
| FlowEngine | proven | Runtime wiring, separate-schema migration, 16 definition/integration cases, unit and mesh coverage |
| Foundry Local inference | proven | Dedicated live 3/3 lane and provider-status contract |
| GitHub Actions and deployment workflow shape | proven | Workflow contract tests and CI execution |
| Bicep module shape | proven | Bicep build and unit contract tests |
| Live Entra or CIAM sign-in | deployment-only | Scaffold auth is the local proof; live app registrations, consent, roles, and redirect URIs require deployment |
| Azure Foundry and Azure AI Search | deployment-only | Provider wiring and gated smoke tests exist; live resources are not required for local acceptance |
| Key Vault backed encryption and data-protection keys | deployment-only | AppHost and Bicep wiring exist; live vault, CMK, identity, and RBAC require deployment |
| Production infrastructure rollout | deployment-only | Deployment workflow and Bicep are validated without asserting a live environment rollout |
| Existing Foundry account, prompt agent, and pre-existing agent opt-ins | documented-only | Commented examples only; not active runtime branches |
| Notifications | not enabled | `includeNotifications: false`; no notification definitions |
| `azd` orchestration | not enabled | `includeAzd: false` |
| Private endpoints | not enabled | `usePrivateEndpoints: false` |

The declared flags and matrix must agree with `.scaffold/resource-implementation.yaml`. Proof paths are validated against the scaffold-owned [TaskFlow proof map](https://github.com/efreeman518/AI-Instructions-Scaffold/blob/main/support/taskflow-proof-map.md).

## Phase Completion

Phases 1 through 5e and the FlowEngine extension are complete. Root `HANDOFF.md` records `workflowStatus: complete`, `currentPhase: 5`, and `currentSubPhase: complete`; future work uses ordinary maintenance.

## Infrastructure as Code

`infra/` contains the Bicep deployment baseline: top-level `main.bicep`, resource modules, deployment scripts, and rollback contracts.

Deployment plan: [`.azure/deployment-plan.md`](../.azure/deployment-plan.md).

Validate locally with `az bicep build --file infra/main.bicep`.

## Outstanding Follow-Ups

1. Authenticated Azure AI persistence and enqueue scenarios remain deployment-only. Close them with a provisioned Azure Foundry and AI Search environment plus real authenticated side-effect assertions; prompt-only model responses are insufficient.
2. Browser WASM trimming remains disabled for Release because upstream Uno dependencies emit `IL2104` under warnings-as-errors. Remove only after a trim-clean dependency update and a clean published cold-start plus normal Uno Playwright run.
