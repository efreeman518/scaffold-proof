# REFERENCE-STATUS - TaskFlow

Current verified status of the TaskFlow reference application. Used by the proof map ([../AI-Instructions-Scaffold/support/taskflow-proof-map.md](../AI-Instructions-Scaffold/support/taskflow-proof-map.md)) and consumers who need an authoritative snapshot of build/test/vulnerability state.

> **Update protocol:** when you commit reference-app changes that move build/test counts or vulnerability state, refresh this file in the same commit. HANDOFF.md narrates session history; this file is the current truth.

## Build Status

| Field | Value |
|---|---|
| Last verified | 2026-08-18 |
| Solution | `TaskFlow.slnx` |
| Target framework | .NET 10 |
| Projects | 44 |
| Errors | 0 |
| Warnings | 0 |

> Note: `src/UI/TaskFlow.Uno/TaskFlow.Uno.csproj` builds separately because Uno.Sdk requires explicit invocation: `dotnet build src/UI/TaskFlow.Uno/TaskFlow.Uno.csproj`.

## Test Status

| Project | Category filter | Verified count | Notes |
|---|---|---:|---|
| Test.Unit | `TestCategory=Unit` | 224 | includes scaffold-auth, deployment/release-manifest contracts, non-destructive migration-model checks, proxy-forwarding, migration-history registration, and shared Aspire deadline-policy tests; verified 2026-08-17 |
| Test.Architecture | `TestCategory=Architecture` | 22 | NetArchTest layering rules; verified 2026-07-16 |
| Test.Endpoints | `TestCategory=Endpoint` | 43 | WebApplicationFactory in-memory contract tests; verified 2026-07-17 |
| Test.E2E | `TestCategory=E2E` | 7 | WebApplicationFactory + Testcontainers SQL workflow chains; verified 2026-07-16 |
| Test.Integration | `TestCategory=Integration` | 28 | service-level tests against real SQL and Azurite Testcontainers, including stable multi-page membership under duplicate business sort keys, schema-pinned EF history, and legacy `dbo` relocation; verified 2026-08-17 |
| Test.Integration.FlowEngine | `TestCategory=Integration` | 16 | workflow JSON validity (deserialize, validator, in-memory registry round-trip, builder, file-presence guard); no Aspire/Docker; verified 2026-07-16 |
| Test.Aspire | multiple (`Aspire`, `Foundry`, `Integration`, `LiveAI`) | 20 | 15 passed; 5 inconclusive because Azure Foundry was not configured. Shared RID-free Aspire mesh covered API, Gateway, Blazor, React, Uno, Functions, audit, and provider topology; verified 2026-08-18 |
| Test.FoundryLocal | `FoundryLocal`, `LiveAI` | 3 | live run: 3 passed against the local model; serial acceptance explicitly opted out this dedicated external-resource lane; verified 2026-08-18 |
| Test.PlaywrightUI | `PlaywrightUI`, `WasmUI`, `Unit` | 9 | published-Release Uno browser coverage plus static-host contracts for required assets, compression quality, MIME preservation, caching, and asset 404 behavior; verified 2026-08-18 |
| Test.UI | `UI`, `Presentation` | 62 | headless presentation and client-contract tests, including source-generated reflection-disabled JSON coverage, Uno navigation/error markup, and the no-login surface; verified 2026-08-18 |
| Test.Mobile | `MobileUI` | 3 | explicitly disabled for serial acceptance; dedicated runner is the enabled-lane gate - 3/3 passed via `run-mobile-tests.ps1` on a live emulator; verified 2026-08-18 |
| Test.Load | `TestCategory=Load` | 2 | NBomber; `[Ignore]` by default; manual run |
| Test.Mutation | n/a | 33 | mutation-target contract tests; verified 2026-07-16 |
| Test.Benchmarks | n/a | - | BenchmarkDotNet console runner; `dotnet run -c Release` |

**Current automated verification:** `dotnet build TaskFlow.slnx --no-restore -m:1` passed across 44 projects with 0 warnings/errors, and the separate Uno build passed across 3 projects with 0 warnings/errors. Unfiltered serial `dotnet test TaskFlow.slnx --no-build -m:1` passed with 462 passed, 0 failed, 10 skipped (5 Azure Foundry-gated, 2 load ignored by default, 3 mobile gated to the dedicated runner); Foundry Local ran live inside acceptance (3 passed) and the suite now includes `UnoWasmCanvasSmoke_Passes` against a fresh stable Uno.Sdk 6.6.42 Release publish. The dedicated mobile runner passed 3/3 against a live Android emulator + Appium. Deployment Dockerfiles use non-root .NET 10 noble-chiseled runtime stages; SDK images are build-stage only. Toolchain: .NET SDK 10.0.400 (global.json), all NuGet pins at latest (EF core packages 1.0.95, EF.FlowEngine/FilterBuilder family 1.0.162, EFCore 10.0.11, MSTest 4.3.3); Refit 15 requires the source-generated client path (`AddRefitGeneratedClient`), which TaskFlow.Blazor now uses. Verified 2026-08-18.

**Uno published-Release proof:** the WASM RootElement startup race (`Arg_NullReferenceException` in `IXamlRootHost.get_RootElement`) fixed upstream in Uno.Sdk 6.6.0-dev.166 is now covered by the stable **Uno.Sdk 6.6.42** pin, which passed the published `Release` first-visit and normal Uno Playwright projects from empty browser state without refresh, retry, sleep, or exception suppression (verified 2026-08-18 with Uno.Extensions 7.2.3). Browser-WASM `Release` temporarily uses `PublishTrimmed=false` because the Navigation/Toolkit/WinUI package set emits upstream `IL2104` trim-analysis failures under warnings-as-errors.

### Playwright (`tests/Test.PlaywrightUI/`)

C# MSTest adapter owns the Aspire graph through `AspireTestHostContext`, resolves named endpoints, and runs C# plus installed TypeScript Playwright projects. It uses one cumulative startup deadline across Docker preflight, Uno restore/build, AppHost startup/readiness, and browser launch. Run `npm ci` inside the folder before first use; endpoint override variables are optional targets, not enable flags.

## Vulnerability Status

Run `dotnet list package --vulnerable --include-transitive` and capture findings here. Severity policy from [../AI-Instructions-Scaffold/support/execution-gates.md](../AI-Instructions-Scaffold/support/execution-gates.md) Section  Vulnerability Audit:

- **High/Critical:** must be fixed or recorded with owner + target resolution date
- **Moderate:** logged here, tracked but not blocking
- **Low:** team discretion

Last audited: 2026-08-17 with `dotnet list TaskFlow.slnx package --vulnerable --include-transitive --no-restore`; no vulnerable packages reported for any project. `MessagePack` remains pinned to `3.1.7` for `Test.Load`; `System.Security.Cryptography.Xml` is pinned to `10.0.11` and `SSH.NET` to `2026.0.0` through `Test.Support`.

| Package | Version | Severity | Direct/Transitive | Advisory | Notes |
|---|---|---|---|---|---|
| _None_ | - | - | - | - | Full solution audit reported no vulnerable packages. |

The solution build currently emits no package vulnerability warnings.

## Phase Completion

Per the consolidated 5-sub-phase taxonomy:

| Phase | Status |
|---|---|
| 1 - Domain Discovery | complete |
| 2 - Resource Definition | complete |
| 3 - Implementation Plan | complete |
| 4 - Contract Scaffolding | complete |
| 5a - Foundation (TDD) | complete |
| 5b - App Core + Runtime/Edge | complete |
| 5c - Optional Hosts | complete (Gateway, Scheduler, Functions, Uno UI, Blazor) |
| 5d - Quality + Delivery | complete (architecture/load/benchmark tests, non-root chiseled Docker runtimes, immutable release manifests, deploy/rollback CI/CD, IaC Bicep) |
| 5e - Integration (Auth + AI) | complete (scaffold mode; live Entra/Foundry deployment-only) |
| 5e+ - Workflow Orchestration | complete (EF.FlowEngine, three shipped workflows, Blazor dashboard, admin API at `/api/flowengine/*`; agent nodes use the Aspire `IChatClient`, with no-op fallback when AI is disabled) |

## AI Runtime Status

Foundry Local verified on 2026-06-13 with:

- Foundry Local `0.8.119`
- Aspire CLI `13.4.3`
- .NET SDK `10.0.300`
- local model `qwen2.5-0.5b` / `FoundryModel.Local.Qwen2505b` (`chat, tools`)

Verified through the Aspire Gateway: D1 basic chat, D2 streaming chat, D3 code-hosted agent, D7 read-only advisor. The Aspire graph now also starts `TaskFlow.Blazor`; `/ai-chat` rendered against the live Gateway URL. D4/D5/D6/D9 side effects still require an authenticated tenant context before they can persist changes.

## Infrastructure as Code (IaC)

`infra/` contains the Bicep deployment baseline:

- `main.bicep` - top-level entry
- `modules/` - SQL, Cosmos DB, Service Bus, Storage, Key Vault (incl. the D-019 Always Encrypted CMK RSA key `taskflow-cmk`, gated by `enableAlwaysEncrypted`), App Configuration, Functions, Container Apps + environment, Static Web App, Log Analytics, deploy identity, role assignment, Cosmos RBAC

Deployment plan: [.azure/deployment-plan.md](.azure/deployment-plan.md).

Validate locally: `az bicep build --file infra/main.bicep`.

## Test Harness Architecture

`Test.Endpoints` and `Test.E2E` derive from a shared `WebApplicationFactoryBase<TProgram, TTrxnContext, TQueryContext>` in `Test.Support` (see `tests/Test.Support/WebApplicationFactoryBase.cs`). The base handles the standard EF.Packages plumbing swap (interceptor removal, pooled-factory removal, scoped-factory removal, reflection-based `DbContext` creation). Derived classes only specify the test-mode store:

- `Test.Endpoints/CustomApiFactory.cs` - InMemoryDatabase per factory instance
- `Test.E2E/SqlApiFactory.cs` - Testcontainers SQL Server, container managed at the class level

## Outstanding Follow-Ups

Tracked here so the next instruction-set or reference-app PR knows what's pending:

1. **Authenticated AI side effects.** D4/D5/D6/D9 persistence/enqueue side effects still require normal tenant/auth context before they can be verified end-to-end.
2. **Uno WASM trimming.** Uno.Sdk is on stable 6.6.42 and first-party code is trim-clean (mock JSON now routes through the source-generated `TaskFlowApiJsonContext`), but Uno.Extensions.Navigation 7.2.3, Uno.Toolkit.WinUI 9.0.3, and Uno.UI (WinUI 6.6.184) still emit IL2104 under warnings-as-errors (verified 2026-08-18). Re-enable `PublishTrimmed` for browser-WASM Release when those packages publish trim-clean annotations.
