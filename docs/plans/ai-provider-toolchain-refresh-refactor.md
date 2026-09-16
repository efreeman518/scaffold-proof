# AI provider and toolchain refresh

## Goal

- Remove the deprecated local AI provider and retain Azure AI Foundry, Azure AI Inference, and OpenAI-compatible runtime paths.
- Upgrade to .NET SDK 10.0.401, current compatible Uno, NuGet, JavaScript, browser, and container tooling.
- Fix refresh-induced warnings and errors and run every non-load verification lane supported by this machine.

## Durable decisions

- Runtime Azure resources are provisioned externally. AppHost accepts either a complete `ConnectionStrings:chat` value or an absolute HTTPS endpoint plus a non-empty deployment, and endpoint-only authentication uses `DefaultAzureCredential`.
- Azure provider request detection covers the resolved provider, opt-in flag, both connection-string key forms, endpoint, and deployment. Partial Azure configuration fails fast; the NonAzure lane rejects Azure provider selection.
- Older pins are allowed only for a reproduced incompatibility with an adjacent removal condition. The tested Aspire SQL 13.5.3 rollback did not fix the Azure graph defect, so the repository retains 13.5.4.
- Uno.Sdk 6.7.22 and Uno.Extensions 7.3.6 are current. Browser-WASM trimming remains disabled only until the upstream Uno package set becomes trim-clean under warnings-as-errors.
- Load tests remain excluded. No commit or push was authorized for this working session.

## Slice status

Active workers: 0.

| Slice | Outcome | Status |
|---|---|---|
| Provider inventory and source-of-truth update | Deprecated provider runtime, configuration, dependency, test, CI, solution, and documentation references removed; Azure wiring retained | Complete |
| Azure configuration correction | Complete connection or endpoint plus deployment accepted; partial/flag-only/provider-only/deployment-only requests rejected; provider and topology contracts updated | Complete |
| SDK, NuGet, Uno, JavaScript, browser, and container refresh | Repository pins updated; authenticated restore and zero-warning builds pass | Complete |
| Generated client refresh | Refitter 2.2.0 regenerated the client; generated output and consumers build | Complete |
| Fast verification | 902 tests pass; analyzer verification passes | Complete |
| Component verification | 213 Azure, NonAzure JSONB, and NonAzure Mongo tests pass | Complete |
| Browser and mobile verification | Full Playwright 8/8 and Android 3/3 pass; iOS compiles but cannot run on Windows | Complete with iOS runtime external |
| Compose and image verification | 8 application images build; 12-service NonAzure smoke passes and cleans up | Complete |
| NonAzure Aspire full graph | 6/6 pass | Complete |
| Azure Aspire full graph | Reproduced Aspire SQL child-health probe-before-create ordering; 13.5.3 rollback also fails | Blocked upstream |
| Live Azure AI Foundry | Five tests require external endpoint, deployment, and credentials | Not run, external |
| Machine-global tooling | Workload updater, administrator Node.js MSI, Android licenses, Functions CLI bootstrap, and Uno.Check false-positive issues remain outside repository control | Blocked external |

## Verification record

- Solution Release restore: 48 projects, 0 warnings.
- Solution Release build: 50 projects, 0 warnings and 0 errors.
- Uno Release restore/build/publish: 3 projects, 0 warnings and 0 errors.
- Fast tests: Unit 544, UI 59, Architecture 77, Endpoints 166, FlowEngine 18, Mutation 33, Playwright unit 5.
- Component tests: Azure Integration 55 and E2E 10; NonAzure JSONB Integration 68 and E2E 10; NonAzure Mongo Integration 70.
- Full Playwright: 8/8 including Blazor, React, Uno WASM, and cold start.
- Android: 3/3. iOS: 3-project compile only.
- NonAzure Aspire: 6/6. Azure Aspire initial exact filter: 0 passed, 7 failed, 3 skipped; blocked before remaining cases by the reproduced child-database health ordering.
- Vulnerability audit: no known vulnerable direct or transitive NuGet packages; React audit reports 0 vulnerabilities.
- Analyzer gate: `dotnet format analyzers ... --verify-no-changes` passed.
- Compose: base plus override valid; all 8 application images built; unique 12-service smoke passed and left no run-owned resources.

## Next action

Only external blockers remain: validate an upstream Aspire SQL release that changes child-database readiness ordering; supply live Azure endpoint, deployment, and credentials for the five cloud tests; use macOS/Xcode for runnable iOS acceptance; and complete administrator/license-gated machine tooling updates.
