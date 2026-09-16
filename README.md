# Scaffold Proof

Reference implementation for [Scaffold AI](https://github.com/efreeman518/scaffold-ai).

## Description

**TaskFlow** - a production-grade reference app demonstrating AI-assisted development patterns for a multi-tenant task management system. Built with Clean Architecture and Domain-Driven Design, runnable through matching Azure and NonAzure hosting lanes.

## Architecture

| Layer | Path | Responsibility |
|-------|------|----------------|
| Domain | `src/Domain/` | Aggregates, entities, domain events |
| Application | `src/Application/` | Use cases, service contracts, message handlers |
| Infrastructure | `src/Infrastructure/` | EF Core, AI, storage, messaging, caching, read models, repositories |
| Host | `src/Host/` | API, Functions, Scheduler, Gateway, Bootstrapper, DatabaseMigrator, Aspire (AppHost + ServiceDefaults), Uno WASM host |
| UI | `src/UI/` | Blazor, React, and Uno (WASM + mobile) clients |
| Shared | `src/Shared/` | Dependency-free cross-cutting libraries (e.g. `TaskFlow.Observability`) |

**Azure lane:** SQL Server 2025, Cosmos DB, Service Bus, Blob Storage/Azurite, Azure Table, and Blob Data Protection.

**NonAzure lane:** PostgreSQL 18 with PostgreSQL JSONB read models by default, RabbitMQ 4, SeaweedFS S3 storage, relational audit, and Redis Data Protection. MongoDB 8 is an explicit read-model alternative. Redis 8, DatabaseMigrator, API, Gateway, Scheduler with embedded TickerQ, Blazor, React, and Uno WASM are common to both lanes; Functions is Azure-only.

**Workflow orchestration:** EF.FlowEngine - three AI-driven workflows (`ai-task-triage`, `ai-task-decomposer`, `compliance-check`) with human-in-the-loop, saga compensation, atomic outbox, and agent nodes backed by the Aspire `IChatClient`; Blazor-hosted Dashboard + Designer; admin REST at `/api/flowengine/*`. See [Tech Design Section 14](docs/tech-design.html#14-workflow-orchestration-flowengine).

Multi-tenant (row-level tenancy). Event-driven async via Service Bus. IaC via Bicep (`infra/`).

**Detailed docs:** [Tech Design](docs/tech-design.html) - [Tech Design Maintenance](docs/TECH-DESIGN-MAINTENANCE.md) - [DESIGN-DECISIONS.md](.scaffold/DESIGN-DECISIONS.md) - [UBIQUITOUS-LANGUAGE.md](.scaffold/UBIQUITOUS-LANGUAGE.md)

## Getting Started

### Prerequisites

- **.NET 10 SDK** - the exact version is pinned by [`global.json`](global.json).
- **Workloads:** `dotnet workload install wasm-tools aspire` (required for the Uno WASM host and the Aspire AppHost).
- **Docker-compatible container runtime** (Docker Desktop, headless Docker Engine, or Podman) - no desktop UI is required. Aspire starts only the selected lane: Azure emulators or PostgreSQL/RabbitMQ/SeaweedFS with optional MongoDB, plus common Redis and application services.
- **Private NuGet feed access:** the `EF.*` (FlowEngine) packages restore from GitHub Packages via the `efreeman518-github` source in [`nuget.config`](nuget.config). Supply a `NUGET_PAT` (a GitHub token with `read:packages`) before restoring.
- **Local tools:** `dotnet tool restore` restores Stryker.NET and the other tools declared in the tool manifest.

### Run

```powershell
dotnet restore TaskFlow.slnx
dotnet run --project src/Host/Aspire/AppHost
```

Use the Aspire dashboard to discover the Gateway, API, and Blazor URLs; ports are allocated per run. See [AI Demos](#ai-demos-azure-ai-foundry-and-openai-compatible-endpoints) for AI-specific run modes.

Local development sends OpenTelemetry only to the Aspire Dashboard. Deployed NonAzure runs OpenObserve OSS `v1.0.0@sha256:d581789cb03b5f061ed56a3e864b5e4bbc86bbf6d317ec863372e98ee49b30d5` in a separate persistent container for logs and traces, with metrics export disabled by default and short operator-configurable retention. Its UI is private by default. All ProblemDetails responses expose the server request identifier as `requestId` and W3C `traceId`/`spanId` values that join structured logs to traces across service and broker hops. This direct SDK export is the minimal baseline; add an OpenTelemetry Collector only if tail sampling becomes necessary. OpenObserve Open Source Edition uses AGPL-3.0; enterprise and hosted offerings are separate products. The single-node Compose shape is low cost, not highly available.

### Providers and container runtime

```powershell
$env:TASKFLOW_LANE = "NonAzure"
$env:TASKFLOW_READMODEL_PROVIDER = "PostgreSqlJsonb" # default; set MongoDb explicitly for that alternative
dotnet test tests/Test.Integration/Test.Integration.csproj
```

`TASKFLOW_TEST_DB_PROVIDER` remains a deprecated compatibility input for one release. New runtime, Aspire, Testcontainers, and CI orchestration uses `TASKFLOW_LANE` and `TASKFLOW_READMODEL_PROVIDER`.

Docker Desktop and headless Docker Engine work without repository-specific configuration when their published ports are reachable on `localhost`. For Podman on Windows/WSL2, use WSL mirrored networking so DCP's loopback-bound ports reach Windows:

```ini
# %UserProfile%\.wslconfig
[wsl2]
networkingMode=mirrored
```

Apply it with `podman machine stop`, `podman machine set --user-mode-networking=false`, `wsl --shutdown`, then `podman machine start`. With mirrored networking, do not set `TESTCONTAINERS_HOST_OVERRIDE`. Under legacy WSL NAT, the component Testcontainers lanes can instead use a run-scoped `TESTCONTAINERS_HOST_OVERRIDE=<podman machine ip>`, but Aspire and full-stack Playwright remain unavailable because DCP publishes container ports to VM loopback.

Generated API clients (Blazor Refit, React `openapi-typescript`) regenerate per [`docs/plans/client-generation.md`](docs/plans/client-generation.md).

### Hosting lanes: Azure vs NonAzure

`TASKFLOW_LANE = Azure | NonAzure` defaults to `Azure`. `Portable` is a deprecated input alias for `NonAzure` for one release. Lane-owned core provider conflicts fail fast instead of creating a mixed topology.

```powershell
dotnet run --project src/Host/Aspire/AppHost                                      # Azure: SQL Server/Cosmos/Service Bus/Azurite
$env:TASKFLOW_LANE = "NonAzure"; dotnet run --project src/Host/Aspire/AppHost     # PostgreSQL JSONB/RabbitMQ/SeaweedFS, zero Azure
$env:TASKFLOW_READMODEL_PROVIDER = "MongoDb"; dotnet run --project src/Host/Aspire/AppHost # explicit MongoDB alternative
```

The NonAzure lane is zero-Azure: non-empty Azure App Configuration and Key Vault service settings are rejected. It uses appsettings/environment configuration, local encryption keys, PostgreSQL, RabbitMQ, SeaweedFS, Redis, and optional MongoDB. Docker Compose places Caddy in front of the same Gateway, Blazor, React, Uno, API, migrator, and Scheduler/TickerQ surfaces as Azure. The Compose files and VPS runbook live under [`deploy/compose/`](deploy/compose/README.md); Azure IaC lives under [`infra/`](infra/README.md).

Every independent provider switch (object storage, read model, audit sink, search, LLM client, Data Protection persistence, Postgres pooler mode) plus its env var, config key, and default is listed in the `Build and test` section of [`AGENTS.md`](AGENTS.md) - that table is the single source, not duplicated here.

### Authentication

The reference app runs with `AuthMode: Scaffold`. The API supplies a fixed authenticated scaffold principal, UI heads do not require or show a login, and anonymous `GET /auth/mode` reports the public mode without exposing provider configuration. This is the executable scaffold proof, not a production security boundary.

Live Entra ID or Entra External ID remains deployment-only. Before public use, implement the chosen client flow, disable scaffold auth, provision registrations/roles/consent, and complete the published-`Release` acceptance steps in [`infra/README.md`](infra/README.md#optional-live-interactive-identity).

## AI Coding Instructions

This repo is the compiled proof for the [scaffold-ai](https://github.com/efreeman518/scaffold-ai) instruction payload, so its agent-facing conventions are part of the reference surface:

- [`AGENTS.md`](AGENTS.md) - single source of maintainer-session instructions, read natively by CLI agents and GitHub Copilot (including VS Code).
- [`CLAUDE.md`](CLAUDE.md) - thin Claude Code entry point that imports `AGENTS.md`.
- [`.mcp.json`](.mcp.json) - Model Context Protocol server configuration (Uno platform + Uno dev server).
- [`.scaffold/`](.scaffold/) - binding source-of-truth artifacts (`domain-specification.yaml`, `UBIQUITOUS-LANGUAGE.md`, `DESIGN-DECISIONS.md`).

## Logging and Code Quality

### Logging strategy

All logging uses the `Microsoft.Extensions.Logging` **`[LoggerMessage]` source generators** rather than runtime-formatted `ILogger` calls. Each project keeps its log definitions in a `LogMessages.cs` file that declares `static partial` methods; the generator emits allocation-free, cached delegates and satisfies analyzer [CA1873](https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1873) (avoid unguarded expensive logging arguments) at the source.

EventIds are centralized in the dependency-free **`TaskFlow.Observability`** shared project ([`src/Shared/TaskFlow.Observability/LogEventIds.cs`](src/Shared/TaskFlow.Observability/LogEventIds.cs)). Every subsystem owns a 1000-wide bucket starting at `10000`, so events stay unique and stable when logs from all hosts are aggregated into a single sink (Application Insights, the Aspire dashboard, Seq, etc.). Declare each event as `<AreaBase> + n` and never renumber a shipped EventId - treat it like public API and deprecate rather than reuse.

| Subsystem | Base constant | Range |
|-----------|---------------|-------|
| API host and middleware | `ApiBase` | 10000-10999 |
| Bootstrapper registration | `BootstrapperBase` | 11000-11999 |
| Gateway (proxy / token service) | `GatewayBase` | 12000-12999 |
| Azure Functions triggers | `FunctionsBase` | 13000-13999 |
| Scheduler background jobs | `SchedulerBase` | 14000-14999 |
| Infrastructure.AI | `InfrastructureAiBase` | 15000-15999 |
| Infrastructure.Storage | `InfrastructureStorageBase` | 16000-16999 |
| Application.Cqrs | `ApplicationCqrsBase` | 17000-17999 |
| Application.Services | `ApplicationServicesBase` | 18000-18999 |
| Application.MessageHandlers | `ApplicationMessageHandlersBase` | 19000-19999 |
| Infrastructure.Messaging.RabbitMq | `InfrastructureMessagingRabbitMqBase` | 20000-20999 |
| Uno WASM static-asset host | `UnoWasmHostBase` | 21000-21999 |
| Infrastructure.Caching | `InfrastructureCachingBase` | 22000-22999 |
| Infrastructure.Data | `InfrastructureDataBase` | 23000-23999 |

The non-zero base avoids colliding with low-numbered EventIds from framework and third-party libraries, and the buckets leave room to grow. `TaskFlow.Observability` intentionally has no dependencies so any layer (domain, application, infrastructure, hosts) can reference it without introducing improper coupling.

### Goal: no errors or warnings

The repository is kept clean at the **error and warning** severities, and CI enforces that gate:

- **Build enforcement:** [`Directory.Build.props`](Directory.Build.props) sets `TreatWarningsAsErrors=true` with `Nullable=enable`, so any warning fails the build for every project in the solution.
- **`src/` logging gate:** [`src/.editorconfig`](src/.editorconfig) (inherits the repo root; does not set `root = true`) sets `dotnet_diagnostic.CA1848.severity = error` for every `.cs` file under `src/`, so a raw `ILogger.Log*()` call fails the build instead of the analyzer-default advisory. `tests/` stays at the default severity so fixtures and test doubles are not forced through source-generated logging.
- **CI analyzer gate:** the `Analyzer cleanliness` step runs `dotnet format analyzers TaskFlow.slnx --severity warn --verify-no-changes --no-restore` on every push and pull request, failing the build if analyzer or code-style diagnostics at `warn` or higher remain.
- **Info-level advisories:** info-severity advisories (for example the CA1873 logging guards) surface in the IDE but do not block CI. The `[LoggerMessage]` strategy above keeps them low; once the source is verified clean at `--severity info`, raise the CI gate to match.

Keep the tree green: prefer fixing the root cause over suppressing a diagnostic, and add a scoped, commented `#pragma`/`.editorconfig` entry only when a suppression is genuinely warranted.

## AI Demos (Azure AI Foundry and OpenAI-compatible endpoints)

The app wires one `Microsoft.Extensions.AI.IChatClient` for every AI demo, including the FlowEngine `ai-agent` connector used by D9. Azure AI Foundry resources and model deployments are provisioned externally, then exposed to Aspire through the `chat` connection. NonAzure deployments can instead use a configured OpenAI-compatible endpoint. Foundry projects and server-hosted agents remain an Azure-only escalation described under *Projects and agents*.

| Mode | How to enable | Model |
|------|---------------|-------|
| OpenAI-compatible endpoint | NonAzure lane with `TASKFLOW_AI_PROVIDER=OpenAICompatible` and its endpoint/key configuration | deployment-specific |
| Azure AI Foundry, keyless | Azure lane with `TASKFLOW_AI_PROVIDER=AzureInference`, `AiServices:FoundryEndpoint`, and `AiServices:AgentModelDeployment` | externally provisioned deployment |
| Azure AI Foundry, connection string | Azure lane with `TASKFLOW_AI_PROVIDER=AzureInference` and complete `ConnectionStrings:chat` containing `Endpoint` and `Deployment`; `Key` is optional | externally provisioned deployment |
| Disabled | default for both hosting lanes; set `TASKFLOW_AI_PROVIDER=None` explicitly when overriding inherited configuration | no-op `IChatClient` (app boots; demos return "not configured") |

### Run With an OpenAI-compatible Endpoint

Use this path for OpenAI, OpenRouter, Ollama, vLLM, or another service that implements the OpenAI wire protocol.

```powershell
$env:TASKFLOW_LANE = "NonAzure"
$env:TASKFLOW_AI_PROVIDER = "OpenAICompatible"
$env:AiServices__Endpoint = "https://example.invalid/v1"
$env:AiServices__ChatModel = "deployment-name"
dotnet run --project src/Host/Aspire/AppHost
```

Supply `AiServices:ApiKey` through the configured secret source. The bootstrapper registers the endpoint as the shared `IChatClient`; D3, D7, and D9 require a deployment that supports tool/function calling.

### Run With Real Azure AI Foundry

Provision the Azure AI Foundry account and model deployment outside this AppHost, using your platform IaC or Azure tooling. Then configure one of these two consumption paths.

```powershell
dotnet user-secrets set "AiServices:FoundryEndpoint" "https://<your-foundry-resource>.services.ai.azure.com/" --project src/Host/Aspire/AppHost
dotnet user-secrets set "AiServices:AgentModelDeployment" "<deployment-name>" --project src/Host/Aspire/AppHost
# or
dotnet user-secrets set "ConnectionStrings:chat" "Endpoint=https://<your-foundry-resource>.services.ai.azure.com/;Deployment=<deployment-name>" --project src/Host/Aspire/AppHost

dotnet run --project src/Host/Aspire/AppHost
```

The endpoint-plus-deployment path injects `Endpoint=...;Deployment=...` and `Aspire.Azure.AI.Inference` authenticates with `DefaultAzureCredential`. A complete connection string can additionally include `Key=...` when key authentication is required. `TASKFLOW_USE_AZURE_FOUNDRY=true` is only an explicit intent flag; it does not supply configuration and fails startup unless one complete path is configured. `aspire publish` does not provision the Foundry account or deployment.

### Run With AI Disabled

Leave the lane default or set `TASKFLOW_AI_PROVIDER=None` to force no-op locally. The app still boots and registers a no-op `IChatClient`. `GET /api/v1/ai/status` reports `provider: none`, D1-D8 return a "not configured" response instead of calling a model, and D9 can start but schema-constrained FlowEngine agent output is expected to fault because the no-op response is not valid model JSON.

### Aspire-backed AI tests

`dotnet test tests/Test.Aspire/Test.Aspire.csproj -m:1 --filter TestCategory=Foundry` boots the AppHost through `Aspire.Hosting.Testing` and runs the Azure live Foundry smoke set. Missing Azure configuration is inconclusive. Once Azure is configured and active, HTTP/provider contract failures remain red. `TASKFLOW_RUN_AZURE_FOUNDRY_TESTS=false` can opt out explicitly. App-level AI HTTP contract coverage lives in `Test.Endpoints` with a fake `IChatClient`.

| Test condition | Result |
|----------------|--------|
| Complete Azure Foundry config exists (`AiServices:FoundryEndpoint` plus `AiServices:AgentModelDeployment`, or complete `ConnectionStrings:chat`) | `Test.Aspire` `TestCategory=Foundry` runs against Azure Foundry |
| Azure Foundry is requested but endpoint or deployment is missing | AppHost configuration fails; the live test does not silently become inconclusive |
| No Azure Foundry config exists | `Test.Aspire` live Foundry tests are inconclusive |

`TestCategory=AzureFoundry` is reserved for Azure-specific provider-selection or provisioning checks. The no-op AI fallback path is covered by unit and endpoint tests. Load, benchmark, and mobile suites stay explicit because they require a running target, BenchmarkDotNet process control, or Appium/emulator setup.

`TASKFLOW_LIVE_AI_BASE_URL` can override the request target for manual live AI smoke runs. It is not a test opt-in.

Scaffold agents should preserve these AI test contracts:

- AI defaults to None in both hosting lanes. AzureInference is Azure-only; OpenAICompatible is NonAzure-only. Provider opt-ins must match the selected lane.
- RID-free suites (`Test.Unit`, `Test.Endpoints`, `Test.Aspire`) use fake clients or explicit Azure configuration; they do not bootstrap native model runtimes.
- Code-hosted agent smoke that does not need tools sends `AgentChatRequest.UseTools=false`; the service maps it to `ChatToolMode.None`. Tool-calling tests must request tools explicitly and carry their own timeout budget.

### CI test lanes

GitHub Actions runs the fast, no-Docker gate on every pull request: Unit, Architecture, Endpoint, and FlowEngine definition tests. PR runs are the merge gate, so there is no duplicate push-to-main trigger. The full `Test.Unit` project has a 50-second process deadline and 15-second blame-hang diagnostics; a timeout fails the job and uploads TRX, sequence, and dump evidence. All Docker-backed, Aspire, browser, Compose, and full-stack acceptance runs only through `workflow_dispatch`.

| Input | Default | Effect |
|-------|---------|--------|
| `lane` | `both` | Runs Azure and NonAzure, or one explicitly selected lane |
| `nonAzureReadModel` | `PostgreSqlJsonb` | Selects the NonAzure JSONB default; `MongoDb` is explicit |
| `includeE2E` | `false` | Runs selected Testcontainers-backed HTTP lane(s) |
| `includeIntegration` | `false` | Runs selected component lane(s) |
| `includeAspireMesh` | `false` | Runs each selected full Aspire graph |
| `includePlaywrightUI` | `false` | Runs Blazor, React, and Uno browser acceptance for each selected lane |
| `includeFullAcceptance` | `false` | Runs unfiltered serial solution acceptance for each selected lane |
| `includeComposeSmoke` | `false` | Builds and smokes the NonAzure Compose lane through Caddy |

`TASKFLOW_TEST_DB_PROVIDER` remains covered as a deprecated alias, but manual orchestration uses only canonical `TASKFLOW_LANE` and `TASKFLOW_READMODEL_PROVIDER` values.

Unfiltered CI acceptance uses explicit false opt-outs for unavailable Functions, Azure Foundry, or mobile lanes. Local acceptance does not require an AI opt-out flag because both hosting lanes default to `None`. Enabled-provider contract failures remain red.

### Aspire-backed UI tests

`dotnet test tests/Test.PlaywrightUI/Test.PlaywrightUI.csproj -m:1` boots the AppHost through `Aspire.Hosting.Testing`, runs the C# Gateway/Blazor happy-path smoke with `Microsoft.Playwright`, and invokes the installed TypeScript Playwright projects for Blazor, React, and Uno.

The C# page objects stay intentionally narrow: Gateway root/`/alive` plus Blazor `/tasks`. React coverage remains DOM/ARIA based. Uno coverage is canvas-first: wait for painted canvas, click stable app chrome, compare visual fingerprints. Do not assert Uno Skia text through DOM selectors. `PLAYWRIGHT_GATEWAY_URL`, `PLAYWRIGHT_BLAZOR_URL`, `PLAYWRIGHT_REACT_URL`, and `PLAYWRIGHT_UNO_URL` are target overrides, not test opt-ins. `AspireTestHostContext` is shared by mesh and Playwright/WASM fixtures and owns Docker preflight, one cumulative startup deadline, named waits, default state/health/exit/timestamp diagnostics, and bounded stop/dispose. `TASKFLOW_ASPIRE_STARTUP_TIMEOUT_SECONDS` or `TASKFLOW_WASM_STARTUP_TIMEOUT_SECONDS` sets the one wall-clock budget; project/test timeouts are shorter caps only. Explicit `TASKFLOW_PLAYWRIGHT_TESTS_ENABLED=false` / `TASKFLOW_WASM_TESTS_ENABLED=false` and failed Docker preflight are inconclusive. Once Docker succeeds, missing selected-lane tooling and AppHost/resource/browser failures are red.

### Projects and agents (opt-in, Azure-only)

The demos above use **code-hosted** agents - a `ChatClientAgent` running in-process over the injected `IChatClient`. That works with every configured provider and boots with the no-op provider when AI is disabled. **Server-hosted** Foundry agents are an Azure-only escalation for hosted memory, centralized tools, or portal/IaC-managed agent definitions. They are not wired by this AppHost.

No `.foundry/agent-metadata.yaml` is committed because no server-hosted agent participates in Foundry deploy/eval workflows yet. When enabling a hosted or prompt agent, create `.foundry/agent-metadata.yaml` under that agent source folder and keep the project endpoint, agent name, datasets, evaluators, and thresholds there.

- **Externally provisioned agents via the client SDK** (bootstrapper-owned provider extension). When an agent is created in the Foundry portal or by IaC, connect to the existing project endpoint and drive it with `AIProjectClient.AsAIAgent(...)`. Add `Azure.AI.Projects` + `Microsoft.Agents.AI.Foundry`, then set `AiServices:FoundryProjectEndpoint` and `AiServices:FoundryAgentName`:

  ```csharp
  var project = new AIProjectClient(new Uri(projectEndpoint), credential);
  // code-first responses agent (no server-side resource created):
  AIAgent agent = project.AsAIAgent(model: deploymentName, name: "TaskAssistant", instructions: prompt);
  // or bind to a pre-existing versioned agent by name:
  var record = await project.AgentAdministrationClient.GetAgentAsync(agentName);
  AIAgent agent = project.AsAIAgent(record);
  ```

  Both results are `Microsoft.Agents.AI.AIAgent`, so `ITaskAssistantAgent` can wrap either path - only construction differs from the code-hosted `ChatClientAgent`.

Use the Aspire dashboard to discover the Gateway and Blazor URLs. Do not hardcode ports; Aspire allocates them per run. D4/D5/D6/D9 write or enqueue side effects and require the normal tenant/auth context. Local scaffold auth provides a predictable development tenant, but production verification should use a real authenticated tenant.

**Demos** (all under `/api/v1/...`, reachable through the gateway; the Aspire-hosted Blazor `AI Chat` page exercises the chat/agent ones):

| # | Concept | Surface |
|---|---------|---------|
| D1 | Basic completion | `POST /api/v1/ai/chat` |
| D2 | Streaming completion (SSE) | `POST /api/v1/ai/chat/stream` |
| D3 | Conversational tool-calling agent | `POST /api/v1/agent/chat` |
| D4 | Structured classification (triage) | `POST /api/v1/ai/triage/{taskId}?apply=true` |
| D5 | Generative enrichment on create | `POST /api/v1/ai/tasks/draft` |
| D6 | Async event-driven inference (readiness review) | created tasks -> Functions handler -> task comment |
| D7 | Read-only multi-tool reasoning | `POST /api/v1/ai/next-action` |
| D8 | Blazor chat UI | `/ai-chat` |
| D9 | Workflow-orchestrated triage | created tasks -> Functions handler -> `ai-task-triage` FlowEngine workflow |

D9 uses the same `IChatClient` as D1-D8. With AI disabled, the workflow still starts but the no-op model response is expected to route the agent node to the faulted output.

## Local Mobile UI Tests

Use `tests/Test.Mobile/run-mobile-tests.ps1` for Android local runs. Test methods do not start Appium or emulators; the runner starts or verifies those dependencies, then enables the mobile lane.

TaskFlow mobile smoke tests use MSTest + Appium for the Uno Android/iOS heads. Android runs locally on Windows; iOS requires macOS or a Mac host with Xcode.

Before building the Android package, restore the Uno app with all mobile targets included:

```powershell
dotnet restore src/UI/TaskFlow.Uno/TaskFlow.Uno.csproj -p:BuildAllUnoTargets=true
dotnet build src/UI/TaskFlow.Uno/TaskFlow.Uno.csproj -p:TargetFrameworkOverride=net10.0-android -p:UseMocks=true --no-restore -m:1
```

The explicit `BuildAllUnoTargets=true` restore is required because the Uno project defaults to a fast Wasm-only restore for local web work. Android/Appium runs need the platform Skia runtime packages in the NuGet asset graph.

Full Appium setup and run commands live in [tests/Test.Mobile/README.md](tests/Test.Mobile/README.md).

## Mutation Testing

TaskFlow has a focused Stryker.NET sample project at [tests/Test.Mutation](tests/Test.Mutation/README.md). It mutates selected domain files and runs MSTest samples that show boundary, failure-message, status-transition, and idempotency checks.

```powershell
dotnet tool restore
dotnet test tests/Test.Mutation/Test.Mutation.csproj
```

Run Stryker from `tests/Test.Mutation`:

```powershell
dotnet tool run dotnet-stryker
```

## Phase 1 Alignment Artifacts

- `.scaffold/domain-specification.yaml`
- `.scaffold/UBIQUITOUS-LANGUAGE.md`
- `.scaffold/DESIGN-DECISIONS.md`
