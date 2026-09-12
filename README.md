# Scaffold Proof

Reference implementation for [Scaffold AI](https://github.com/efreeman518/scaffold-ai).

## Description

**TaskFlow** - a production-grade reference app demonstrating AI-assisted development patterns for a multi-tenant task management system. Built with Clean Architecture and Domain-Driven Design, running local with Aspire, deployable to Azure.

## Architecture

| Layer | Path | Responsibility |
|-------|------|----------------|
| Domain | `src/Domain/` | Aggregates, entities, domain events |
| Application | `src/Application/` | Use cases, service contracts, message handlers |
| Infrastructure | `src/Infrastructure/` | EF Core, Azure AI, Storage, Repos |
| Host | `src/Host/` | API, Functions, Scheduler, Gateway, Bootstrapper, DatabaseMigrator, Aspire (AppHost + ServiceDefaults), Uno WASM host |
| UI | `src/UI/` | Blazor, React, and Uno (WASM + mobile) clients |
| Shared | `src/Shared/` | Dependency-free cross-cutting libraries (e.g. `TaskFlow.Observability`) |

**Azure services:** SQL Server, Cosmos DB, Service Bus, Blob Storage, Azure AI Search, Microsoft Foundry.

**Workflow orchestration:** EF.FlowEngine - three AI-driven workflows (`ai-task-triage`, `ai-task-decomposer`, `compliance-check`) with human-in-the-loop, saga compensation, atomic outbox, and agent nodes backed by the Aspire `IChatClient`; Blazor-hosted Dashboard + Designer; admin REST at `/api/flowengine/*`. See [Tech Design Section 14](docs/tech-design.html#14-workflow-orchestration-flowengine).

Multi-tenant (row-level tenancy). Event-driven async via Service Bus. IaC via Bicep (`infra/`).

**Detailed docs:** [Tech Design](docs/tech-design.html) - [Tech Design Maintenance](docs/TECH-DESIGN-MAINTENANCE.md) - [DESIGN-DECISIONS.md](.scaffold/DESIGN-DECISIONS.md) - [UBIQUITOUS-LANGUAGE.md](.scaffold/UBIQUITOUS-LANGUAGE.md)

## Getting Started

### Prerequisites

- **.NET 10 SDK** - the exact version is pinned by [`global.json`](global.json).
- **Workloads:** `dotnet workload install wasm-tools aspire` (required for the Uno WASM host and the Aspire AppHost).
- **Docker-compatible container runtime** (Docker Desktop, headless Docker Engine, or Podman) - no desktop UI is required. The Aspire AppHost runs SQL Server, Azurite, Service Bus, Redis, and Cosmos DB emulators locally, and the container-backed test lanes need published ports reachable from the test process.
- **Private NuGet feed access:** the `EF.*` (FlowEngine) packages restore from GitHub Packages via the `efreeman518-github` source in [`nuget.config`](nuget.config). Supply a `NUGET_PAT` (a GitHub token with `read:packages`) before restoring.
- **Local tools:** `dotnet tool restore` restores Stryker.NET and the other tools declared in the tool manifest.

### Run

```powershell
dotnet restore TaskFlow.slnx
dotnet run --project src/Host/Aspire/AppHost
```

Use the Aspire dashboard to discover the Gateway, API, and Blazor URLs; ports are allocated per run. See [AI Demos](#ai-demos-azure-ai-foundry-and-foundry-local) for AI-specific run modes.

### Providers and container runtime

```powershell
$env:TASKFLOW_DB_PROVIDER = "PostgreSql"       # runtime DB provider: SqlServer (default) | PostgreSql
$env:TASKFLOW_TEST_DB_PROVIDER = "PostgreSql"  # container-backed test lanes: SqlServer (default) | PostgreSql
$env:TASKFLOW_MESSAGING_PROVIDER = "RabbitMq"  # messaging transport: ServiceBus (default) | RabbitMq
dotnet test tests/Test.Integration/Test.Integration.csproj
```

Docker Desktop and headless Docker Engine work without repository-specific configuration when their published ports are reachable on `localhost`. For Podman on Windows/WSL2, use WSL mirrored networking so DCP's loopback-bound ports reach Windows:

```ini
# %UserProfile%\.wslconfig
[wsl2]
networkingMode=mirrored
```

Apply it with `podman machine stop`, `podman machine set --user-mode-networking=false`, `wsl --shutdown`, then `podman machine start`. With mirrored networking, do not set `TESTCONTAINERS_HOST_OVERRIDE`. Under legacy WSL NAT, the component Testcontainers lanes can instead use a run-scoped `TESTCONTAINERS_HOST_OVERRIDE=<podman machine ip>`, but Aspire and full-stack Playwright remain unavailable because DCP publishes container ports to VM loopback.

Generated API clients (Blazor Refit, React `openapi-typescript`) regenerate per [`docs/plans/client-generation.md`](docs/plans/client-generation.md).

### Hosting lanes: Azure vs Portable

`TASKFLOW_LANE = Azure | Portable` (default `Azure`) seeds the default of every provider switch below; each switch's own env var or config key still wins over the lane default, and no registration site reads the lane directly (D-035).

```powershell
dotnet run --project src/Host/Aspire/AppHost                                     # Azure lane (default): SQL Server/Cosmos/Service Bus/Blob/Azure AI Search emulators
$env:TASKFLOW_LANE = "Portable"; dotnet run --project src/Host/Aspire/AppHost     # Portable lane: Postgres + RabbitMQ + MinIO, no Azure emulators
```

The Portable lane is the non-Azure hosting target: Docker Compose on a single VPS, Caddy as the TLS edge in front of the YARP gateway, PostgreSQL + RabbitMQ + MinIO (S3-compatible object storage) as containers, Azure retained only for Key Vault (DEK wrap, Data Protection key protection) and App Configuration (config + feature flags). The Compose files, environment template, and VPS deploy runbook live under [`deploy/compose/`](deploy/compose/README.md); the Azure IaC counterpart (including the Bicep `pgbouncer` param that mirrors the Portable lane's pooler switch) is in [`infra/README.md`](infra/README.md).

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

## AI Demos (Azure AI Foundry and Foundry Local)

The app wires one `Microsoft.Extensions.AI.IChatClient` for every AI demo, including the FlowEngine `ai-agent` connector used by D9. Azure Foundry is still modeled by Aspire as a `chat` deployment. Foundry Local is temporarily bootstrapped by `TaskFlow.Bootstrapper` with `Microsoft.AI.Foundry.Local` because Aspire `RunAsFoundryLocal()` is avoided until its bundled SDK can discover the GA runtime. Two independent axes apply: **lifecycle** (where the Foundry resource comes from) and **consumption** (this app consumes raw model inference; Foundry projects + server-hosted agents are an Azure-only escalation, documented as commented opt-ins - see *Projects and agents* below).

| Mode | How to enable | Model |
|------|---------------|-------|
| Foundry Local (on-device, no Azure) | default when Azure Foundry is not configured and `AiServices:DisableFoundryLocal` is false | `qwen2.5-0.5b` |
| Provision new Azure AI Foundry | set `AiServices:FoundryEndpoint` (config/user-secrets) or `TASKFLOW_USE_AZURE_FOUNDRY=true` with Azure provisioning configured | `FoundryModel.OpenAI.Gpt4oMini` |
| Connect to existing Azure AI Foundry | uncomment the `RunAsExisting` block in `AppHost.cs` and set `AiServices:FoundryResourceName` + `AiServices:FoundryResourceGroup` (the `chat` deployment must already exist there) | `FoundryModel.OpenAI.Gpt4oMini` |
| Disabled | set `AiServices:DisableFoundryLocal=true`, or leave Azure absent and local bootstrap unavailable | no-op `IChatClient` (app boots; demos return "not configured") |
| Publish | always | provisions a real Azure Foundry resource |

### Run Fully Local With Foundry Local

Use this path when you want the AI demos to call a local model and avoid Azure model calls.

```powershell
dotnet run --project src/Host/Aspire/AppHost
```

The AppHost wires no `chat` resource for local mode. When Azure is absent and `AiServices:DisableFoundryLocal` is false, the bootstrapper uses `Microsoft.AI.Foundry.Local` directly, downloads execution providers and the `qwen2.5-0.5b` model if needed, starts its OpenAI-compatible endpoint at `AiServices:LocalWebUrl` (default `http://127.0.0.1:52415`), records `AiProviderInfo("local")`, and registers it as the shared `IChatClient`. If Foundry Local cannot bootstrap, the bootstrapper logs the failure and falls back to no-op AI with `AiProviderInfo("none")`.

Use `qwen2.5-0.5b` because D3, D7, and D9 exercise tool/function calling. First run can be slow because the SDK downloads execution providers and the model into the local Foundry cache.

### Run With Real Azure AI Foundry

Use this path when you have a real Azure AI Foundry deployment or when testing the publish shape.

```powershell
dotnet user-secrets set "AiServices:FoundryEndpoint" "https://<your-foundry-resource>.services.ai.azure.com/" --project src/Host/Aspire/AppHost
# or
$env:TASKFLOW_USE_AZURE_FOUNDRY = "true"

dotnet run --project src/Host/Aspire/AppHost
```

In this mode the AppHost calls `AddFoundry("foundry").AddDeployment("chat", FoundryModel.OpenAI.Gpt4oMini)`. `aspire publish` always takes the real Azure path and provisions an Azure AI Foundry resource/deployment. If your Azure tenant requires keyless managed-identity auth for the inference client, adjust the host registration around `AddAzureChatCompletionsClient("chat")` to pass the required credential shape before treating local failures as model failures.

### Run With AI Disabled

Set `AiServices:DisableFoundryLocal=true` to force no-op locally. When no provider is active, the app still boots and registers a no-op `IChatClient`. `GET /api/v1/ai/status` reports `provider: none`, D1-D8 return a "not configured" response instead of calling a model, and D9 can start but schema-constrained FlowEngine agent output is expected to fault because the no-op response is not valid model JSON.

### Aspire-backed AI tests

`dotnet test tests/Test.Aspire/Test.Aspire.csproj -m:1 --filter TestCategory=Foundry` boots the AppHost through `Aspire.Hosting.Testing` and runs the Azure live Foundry smoke set. Missing Azure configuration is inconclusive. Once Azure is configured and active, HTTP/provider contract failures remain red. `TASKFLOW_RUN_AZURE_FOUNDRY_TESTS=false` can opt out explicitly. The Aspire mesh is RID-free and forces `AiServices:DisableFoundryLocal=true`, so it never starts a local native model. App-level AI HTTP contract coverage lives in `Test.Endpoints` with a fake `IChatClient`.

`dotnet test tests/Test.FoundryLocal/Test.FoundryLocal.csproj -m:1 --filter TestCategory=FoundryLocal` runs the RID-bound local smoke lane. It boots `TaskFlow.Api` directly, checks `GET /api/v1/ai/status` for `provider: local`, then covers chat, no-tool agent chat, and one safe write-adjacent AI demo. Missing or undiscoverable runtime and post-bootstrap model timeouts are inconclusive. Startup failures after runtime discovery, provider mismatch, and HTTP/JSON contract failures remain red. `TASKFLOW_RUN_FOUNDRY_LOCAL_TESTS=false` can opt out explicitly.

| Test condition | Result |
|----------------|--------|
| Azure Foundry config exists (`AiServices:FoundryEndpoint` or `TASKFLOW_USE_AZURE_FOUNDRY=true`) | `Test.Aspire` `TestCategory=Foundry` runs against Azure Foundry |
| No Azure Foundry config exists | `Test.Aspire` live Foundry tests are inconclusive |
| Foundry Local SDK bootstraps in the RID-bound API host | `Test.FoundryLocal` `TestCategory=FoundryLocal` runs against the local model |
| Foundry Local runtime missing or undiscoverable | `Test.FoundryLocal` is inconclusive |

`TestCategory=AzureFoundry` is reserved for Azure-specific provider-selection or provisioning checks. The no-op AI fallback path is covered by unit and endpoint tests. Load, benchmark, and mobile suites stay explicit because they require a running target, BenchmarkDotNet process control, or Appium/emulator setup.

`TASKFLOW_LIVE_AI_BASE_URL` can override the request target for manual live AI smoke runs. It is not a test opt-in.

Scaffold agents should preserve these AI test contracts:

- Provider order is Azure Foundry first, then Foundry Local, then no-op. Azure is active only when Aspire injects `ConnectionStrings:chat` or Azure Foundry config is set. Foundry Local is active when Azure is absent and `AiServices:DisableFoundryLocal=false`.
- RID-free suites (`Test.Unit`, `Test.Endpoints`, `Test.Aspire`) do not start native Foundry Local. They use fake clients or `AiServices:DisableFoundryLocal=true`.
- `Test.FoundryLocal` is the only RID-bound local model lane. Missing runtime and bootstrapped-but-slow model calls are inconclusive. Runtime startup failures after discovery, provider mismatch, no-op fallback, and HTTP/JSON contract failures are red.
- Code-hosted agent smoke that does not need tools sends `AgentChatRequest.UseTools=false`; the service maps it to `ChatToolMode.None`. Tool-calling tests must request tools explicitly and carry their own timeout budget.

### CI test lanes

GitHub Actions runs the fast, no-Docker gate on every pull request: Unit, Architecture, Endpoint, and FlowEngine definition tests. PR runs are the merge gate, so there is no separate push trigger. The heavier lanes run automatically on a monthly schedule (28th of the month, 06:17 UTC - the last day that exists in every month, since GitHub Actions cron cannot express "last day of month") and can also be launched on demand through `workflow_dispatch` inputs:

| Input | Test project | Trigger | Notes |
|-------|--------------|---------|-------|
| `includeE2E` | `Test.E2E` | Monthly + manual | SQL-backed HTTP workflows; requires Docker |
| `includeIntegration` | `Test.Integration` | Monthly + manual | SQL + Azurite component tests; requires Docker |
| `includeAspireMesh` | `Test.Aspire` | Monthly + manual | Full Aspire AppHost graph; CI explicitly opts out Azure Foundry smoke unless configured |
| `includeFoundryLocal` | `Test.FoundryLocal` | Manual only | RID-bound Foundry Local live smoke; may download local model assets |
| `includePlaywrightUI` | `Test.PlaywrightUI` | Manual only | Browser smoke path; restores Playwright and React npm packages |
| `includeFullAcceptance` | Entire solution | Manual only | Unfiltered `dotnet test TaskFlow.slnx --no-build -m:1`; provisions browser/React prerequisites, while the WASM fixture owns restore/build inside its startup deadline |

The monthly run keeps the Docker-backed E2E, Integration, and Aspire lanes green at minimal Actions-minute cost; Foundry Local and Playwright UI stay dispatch-only for cost and flakiness reasons.

Unfiltered CI acceptance uses explicit false opt-outs for unavailable Functions, Azure Foundry, Foundry Local, or mobile lanes. Local acceptance does not require AI opt-out flags: absent Azure/Foundry Local resources and bootstrapped-but-slow local generation are inconclusive. Enabled-provider contract failures remain red.

### Aspire-backed UI tests

`dotnet test tests/Test.PlaywrightUI/Test.PlaywrightUI.csproj -m:1` boots the AppHost through `Aspire.Hosting.Testing`, runs the C# Gateway/Blazor happy-path smoke with `Microsoft.Playwright`, and invokes the installed TypeScript Playwright projects for Blazor, React, and Uno.

The C# page objects stay intentionally narrow: Gateway root/`/alive` plus Blazor `/tasks`. React coverage remains DOM/ARIA based. Uno coverage is canvas-first: wait for painted canvas, click stable app chrome, compare visual fingerprints. Do not assert Uno Skia text through DOM selectors. `PLAYWRIGHT_GATEWAY_URL`, `PLAYWRIGHT_BLAZOR_URL`, `PLAYWRIGHT_REACT_URL`, and `PLAYWRIGHT_UNO_URL` are target overrides, not test opt-ins. `AspireTestHostContext` is shared by mesh and Playwright/WASM fixtures and owns Docker preflight, one cumulative startup deadline, named waits, default state/health/exit/timestamp diagnostics, and bounded stop/dispose. `TASKFLOW_ASPIRE_STARTUP_TIMEOUT_SECONDS` or `TASKFLOW_WASM_STARTUP_TIMEOUT_SECONDS` sets the one wall-clock budget; project/test timeouts are shorter caps only. Explicit `TASKFLOW_PLAYWRIGHT_TESTS_ENABLED=false` / `TASKFLOW_WASM_TESTS_ENABLED=false` and failed Docker preflight are inconclusive. Once Docker succeeds, missing selected-lane tooling and AppHost/resource/browser failures are red.

### Projects and agents (opt-in, Azure-only)

The demos above use **code-hosted** agents - a `ChatClientAgent` running in-process over the injected `IChatClient`. That works with every lifecycle mode and boots offline. **Server-hosted** Foundry agents are an Azure-only escalation for hosted memory, centralized tools, or portal/IaC-managed agent definitions. They are documented and wired as commented opt-ins; nothing here runs by default.

No `.foundry/agent-metadata.yaml` is committed because no server-hosted agent participates in Foundry deploy/eval workflows yet. When enabling a hosted or prompt agent, create `.foundry/agent-metadata.yaml` under that agent source folder and keep the project endpoint, agent name, datasets, evaluators, and thresholds there.

- **Aspire-modeled project + prompt agent** (commented in `AppHost.cs`). A project (`foundry.AddProject(...)`) is the container for server-hosted agents, deployments, and tool connections. A prompt agent (`project.AddPromptAgent(model, "name", instructions).WithTool(...)`) is declarative. Tools are project-level resources (code interpreter, web/AI-Search/Bing grounding, function calling). Note: **prompt agents always deploy to Azure Foundry, even under `aspire run`** - there is no offline path. Referencing the project injects `PROJ_URI` (the project endpoint) into the consuming host.

- **Pre-existing agents via the client SDK** (bootstrapper-owned provider extension). When an agent is created in the Foundry portal or by IaC, connect to the existing project endpoint and drive it with `AIProjectClient.AsAIAgent(...)`. Add `Azure.AI.Projects` + `Microsoft.Agents.AI.Foundry`, set `AiServices:FoundryProjectEndpoint` (or read the Aspire-injected `PROJ_URI`) and `AiServices:FoundryAgentName`:

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
