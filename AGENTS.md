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
| Infrastructure | `src/Infrastructure/` (EF Core, AI, Storage, Repositories) |
| Hosts | `src/Host/` (Api, Gateway, Scheduler, Functions, DatabaseMigrator, Bootstrapper, Aspire, Uno WASM) |
| Tests | `tests/` (15 projects; see REFERENCE-STATUS.md for verified counts) |

## Build and test

```powershell
dotnet build TaskFlow.slnx                                     # 0 warnings expected
dotnet build src/UI/TaskFlow.Uno/TaskFlow.Uno.csproj           # Uno builds separately (Uno.Sdk)
dotnet test TaskFlow.slnx --filter "TestCategory=Unit"         # fast lane
dotnet test TaskFlow.slnx --filter "TestCategory=Architecture|TestCategory=Endpoint"
dotnet test TaskFlow.slnx --no-build -m:1                     # unfiltered serial acceptance; full-stack projects are resource-heavy
# E2E/Integration need a container runtime (Podman-backed Docker context verified)
dotnet run --project src/Host/Aspire/AppHost                   # full local stack
```

## Pointers

- Current verified build/test/vulnerability state: `.scaffold/REFERENCE-STATUS.md`
- Detailed design: `docs/tech-design.html` (maintenance rules: `docs/TECH-DESIGN-MAINTENANCE.md`)
- Deployment: `infra/` (Bicep), `.azure/deployment-plan.md`
