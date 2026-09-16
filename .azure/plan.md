# Azure Deployment Plan

> **Status:** Complete

Generated: 2026-06-19

---

## 1. Project Overview

**Goal:** Implement Azure AI Foundry behind one `Microsoft.Extensions.AI.IChatClient`; this historical plan also evaluated a local-model experiment that was later removed.

**Path:** Add Components

---

## 2. Requirements

| Attribute | Value |
|-----------|-------|
| Classification | Development |
| Scale | Small |
| Budget | Cost-Optimized |
| Subscription | Not required for this code-only change |
| Location | Not required for this code-only change |

---

## 3. Components Detected

| Component | Type | Technology | Path |
|-----------|------|------------|------|
| TaskFlow.Api | API | ASP.NET Core, .NET Aspire | `src/Host/TaskFlow.Api` |
| AppHost | Orchestrator | .NET Aspire | `src/Host/Aspire/AppHost` |
| TaskFlow.Infrastructure.AI | AI services | Microsoft.Extensions.AI, Microsoft Agent Framework | `src/Infrastructure/TaskFlow.Infrastructure.AI` |
| Test.Aspire | Integration tests | MSTest, Aspire.Hosting.Testing | `src/Test/Test.Aspire` |

---

## 4. Recipe Selection

**Selected:** Existing .NET Aspire app code.

**Rationale:** The repository already contains Aspire orchestration and Azure AI Foundry package references. The requested work is code integration and test verification, not deployment artifact generation.

---

## 5. Architecture

**Stack:** ASP.NET Core API orchestrated by Aspire.

### Service Mapping

| Component | Azure Service | SKU |
|-----------|---------------|-----|
| TaskFlow.Api chat client | Azure AI Foundry deployment when `ConnectionStrings:chat` exists | Existing Aspire default |
| Removed local-model experiment | API-host bootstrap evaluated historically, no longer shipped | Removed |

### Supporting Services

| Service | Purpose |
|---------|---------|
| Microsoft.Extensions.AI | Common `IChatClient` abstraction |
| Aspire.Azure.AI.Inference | Azure Foundry chat completions client |
| Removed experimental SDK | Temporary API-host bootstrap evaluated historically, no longer shipped |
| OpenAI SDK | OpenAI-compatible inference client |

---

## 6. Execution Checklist

### Phase 1: Planning
- [x] Analyze workspace
- [x] Gather requirements
- [x] Confirm subscription and location are not needed for this code-only change
- [x] Scan codebase
- [x] Select recipe
- [x] Plan architecture
- [x] User approved this scoped implementation in chat

### Phase 2: Execution
- [x] Research components
- [x] Add package versions and API-host references
- [x] Implement the now-removed API-host local-model experiment
- [x] Keep the experimental bootstrapping in the API host rather than the AppHost
- [x] Split Azure Aspire smoke from the now-removed RID-bound experiment
- [x] Update plan status to `Ready for Validation`

### Phase 3: Validation
- [x] Build affected projects
- [x] Retire the removed experiment's RID-bound smoke tests
- [x] Record validation proof below

---

## 7. Validation Proof

| Check | Command Run | Result | Timestamp |
|-------|-------------|--------|-----------|
| Build affected projects | `rtk dotnet build src\Test\Test.Aspire\Test.Aspire.csproj -m:1` | Passed, 25 projects, 0 errors, 1 warning | 2026-06-19 01:43:31 -04:00 |
| Triage parse guard unit | `rtk dotnet test src\Test\Test.Unit\Test.Unit.csproj --filter FullyQualifiedName~TriageAsync_WithNullSuggestedPriority_ReturnsParseGuardError` | Passed, 1 test | 2026-06-19 01:43:31 -04:00 |
| Removed local-model experiment tests | Historical RID-bound smoke command removed with the experiment | Was the dedicated experiment lane | 2026-06-20 |

---

## 8. Files to Generate

| File | Purpose | Status |
|------|---------|--------|
| `.azure/plan.md` | Required Azure plan | Done |
| Removed experimental bootstrap source | Shared local-model bootstrap at the time; later deleted | Removed |

---

## 9. Next Steps

Current: implementation and validation complete.
