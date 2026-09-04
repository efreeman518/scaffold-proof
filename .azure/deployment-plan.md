# Azure Deployment Plan

> **Status:** Ready for Validation

Generated: 2026-04-23

---

## 1. Project Overview

**Goal:** Deploy TaskFlow multi-tenant task management application to a single Azure dev environment. All backend services containerized in Azure Container Apps, Functions on Flex Consumption, minimal SKUs, managed identities, App Configuration + Key Vault for config/secrets.

**Path:** Modernize Existing - adding Azure deployment infrastructure to an existing .NET Aspire-orchestrated application.

---

## 2. Requirements

| Attribute | Value |
|-----------|-------|
| Classification | Development |
| Scale | Small (single dev environment) |
| Budget | Cost-Optimized - target <$50/mo |
| **Subscription** | `db98b283-631e-4f24-bd77-321332820725` |
| **Location** | `eastus2` |

---

## 3. Components Detected

| Component | Type | Technology | Path |
|-----------|------|------------|------|
| TaskFlow.Gateway | API Gateway (YARP) | ASP.NET Core, .NET 10 | `src/Host/TaskFlow.Gateway/` |
| TaskFlow.Api | Backend API | ASP.NET Core Minimal APIs, EF Core 10 | `src/Host/TaskFlow.Api/` |
| TaskFlow.Scheduler | Background Worker | ASP.NET Core, TickerQ | `src/Host/TaskFlow.Scheduler/` |
| TaskFlow.Functions | Event Processor | Azure Functions Isolated Worker v4 | `src/Host/TaskFlow.Functions/` |
| TaskFlow.Blazor | Web UI | .NET 10 Interactive Server, MudBlazor | `src/UI/TaskFlow.Blazor/` |
| TaskFlow.Uno | Cross-platform UI | Uno Platform WASM | `src/UI/TaskFlow.Uno/` |

---

## 4. Recipe Selection

**Selected:** Bicep (standalone, no AZD)

**Rationale:**
- User requested Bicep IaC
- Full control over resource definitions and modular structure
- No AZD wrapper - deployment via `az deployment` + GitHub Actions
- Aspire used only for local dev orchestration, not cloud deployment

---

## 5. Architecture

**Stack:** Containers (Container Apps) + Serverless (Functions Flex Consumption)

### Compute Service Mapping

| Component | Azure Service | Dev scale (`main.dev.bicepparam`) | Prod scale (`main.prod.bicepparam`) | Ingress |
|-----------|---------------|------------------------------------|---------------------------------------|---------|
| TaskFlow.Gateway | Container App | min 0 / max 2, 0.25 vCPU, 0.5Gi | min 2 / max 100, 50 concurrent requests, 0.5 vCPU, 1Gi | External (only public endpoint) |
| TaskFlow.Api | Container App | min 0 / max 3, 0.5 vCPU, 1Gi | min 2 / max 100, 50 concurrent requests, 1.0 vCPU, 2Gi | Internal only |
| TaskFlow.Scheduler | Container App | min 0 / max 1, 0.25 vCPU, 0.5Gi | min 2 / max 2 (always-on, no concurrency rule), 0.5 vCPU, 1Gi | Internal only (no ingress) |
| TaskFlow.Functions | Functions Flex Consumption | `functionAppScaleLimit` 20 | `functionAppScaleLimit` 20 | Internal (Service Bus trigger) |
| TaskFlow.Blazor | Container App | min 0 / max 1, 0.25 vCPU, 0.5Gi | min 2 / max 30, 0.5 vCPU, 1Gi | External |
| TaskFlow.Uno | Container App (static serve) | Consumption (0.25 vCPU, 0.5Gi) | Consumption (0.25 vCPU, 0.5Gi) | External |

### Data & Messaging Service Mapping

| Service | Azure Resource | SKU/Tier | Est. $/mo |
|---------|---------------|----------|-----------|
| Database (`databaseProvider`) | Azure SQL Database (default) or PostgreSQL Flexible Server 17 | Dev: Basic DTU (5 DTU) / Postgres `Standard_B1ms` Burstable. Prod: SQL Hyperscale `HS_Gen5_2` zone-redundant + HA/read-scale replica, or Postgres `GeneralPurpose` zone-redundant + read replica | Dev ~$5; prod materially higher (Hyperscale/HA priced per vCore + replica) |
| Cache | Azure Managed Redis (`Microsoft.Cache/redisEnterprise`), FusionCache L2 (`ConnectionStrings__Redis1`) | Dev `Balanced_B0`, no HA. Prod `Balanced_B5`+, HA | Dev ~$0 (~small Balanced tier); prod higher with HA |
| Document Store | Cosmos DB | Serverless | ~$0-5 |
| Messaging | Service Bus | Standard (3 filtered subscriptions: `projection`, `ai-review`, `workflow`) | ~$10 |
| Blob Storage | Storage Account (Blob + Tables) | Standard LRS | ~$1 |
| Functions Storage | Storage Account | Standard LRS | ~$1 |

### Platform Services

| Service | Purpose | SKU/Tier | Est. $/mo |
|---------|---------|----------|-----------|
| Container Apps Environment | Hosts all containers | Consumption | ~$0-5 |
| App Configuration | Centralized config | Free | $0 |
| Key Vault | Secrets management | Standard | ~$0 |
| Log Analytics Workspace | Centralized logging | Pay-as-you-go (5GB free) | ~$0 |
| User-Assigned Managed Identity | GitHub Actions OIDC deploy | N/A | $0 |
| Container Registry | N/A - using ghcr.io | N/A | $0 |

**Total estimated: ~$17-27/mo**

### Identity & Access (Managed Identities + RBAC)

The baseline deployment keeps end-user `AuthMode: Scaffold` so the reference app remains runnable without a login or tenant provisioning. Managed identities below secure Azure resource access; they do not convert the scaffold principal into production user authentication. Live interactive Entra/CIAM setup is an explicit deployment hardening step documented in `infra/README.md` and must disable scaffold auth before public use.

| Identity | Assigned To | Roles |
|----------|------------|-------|
| System MI (Gateway) | Gateway Container App | App Configuration Data Reader, Key Vault Secrets User |
| System MI (API) | API Container App | Database Contributor (Entra auth, provider-dependent), Service Bus Data Sender, Storage Blob Data Contributor, Cosmos DB Data Contributor, App Configuration Data Reader, Key Vault Secrets User, Redis Entra access policy assignment |
| System MI (Scheduler) | Scheduler Container App | Database Contributor, Service Bus Data Sender, App Configuration Data Reader, Key Vault Secrets User, Redis Entra access policy assignment |
| System MI (Functions) | Functions App | Service Bus Data Receiver, Storage Blob Data Contributor, Cosmos DB Data Contributor, Database Contributor |
| User-Assigned MI | GitHub Actions | Contributor (RG scope), User Access Administrator (RG scope) |

Redis access policy assignments grant the API/Scheduler managed identities Entra data-plane permission on the
default database, but `ConnectionStrings__Redis1` still authenticates with the access key today: StackExchange.Redis
needs the `Microsoft.Azure.StackExchangeRedis` token-provider package wired in application code to use the Entra
grant, which is out of scope for this infra-only change. Same pre-existing gap as SQL/Postgres: no per-identity
database user/role provisioning yet - see the module comments in `sql-database.bicep`/`postgres-flexible-server.bicep`.

### Networking

- **No Front Door** (dev budget constraint)
- **No VNet / Private Endpoints** (dev budget constraint - minimal SKUs don't support)
- Gateway: external ingress (public), all other Container Apps: internal only
- Services secured via: managed identity, service-level firewall rules (allow Azure services)
- SQL: firewall rule allowing Azure services
- Storage/Cosmos/Key Vault/App Config: firewall rules + managed identity auth

### Config & Secrets Strategy

| Config Type | Store | Access Method |
|-------------|-------|---------------|
| App settings (non-secret) | App Configuration | Managed Identity -> `Azure App Configuration` SDK |
| Connection strings, API keys | Key Vault | Key Vault references from App Configuration |
| Service endpoints | Container App env vars | Bicep output wiring |

---

## 6. Provisioning Limit Checklist

| Resource Type | Count | Notes |
|---------------|-------|-------|
| Microsoft.App/managedEnvironments | 1 | Consumption tier |
| Microsoft.App/containerApps | 5 | Gateway, API, Scheduler, Blazor, Uno |
| Microsoft.Sql/servers or Microsoft.DBforPostgreSQL/flexibleServers | 1 | Entra-only auth; whichever `databaseProvider` is selected (mutually exclusive) |
| Microsoft.Sql/servers/databases or .../flexibleServers/databases | 1 (+1 read replica in prod) | Dev Basic DTU / Postgres Burstable; prod Hyperscale HA replica or Postgres read replica |
| Microsoft.Cache/redisEnterprise + /databases | 1 + 1 | Azure Managed Redis (FusionCache L2) |
| Microsoft.DocumentDB/databaseAccounts | 1 | Serverless |
| Microsoft.ServiceBus/namespaces | 1 | Standard, 3 topic subscriptions (`projection`, `ai-review`, `workflow`) |
| Microsoft.Storage/storageAccounts | 2 | App data + Functions runtime |
| Microsoft.Web/sites (Function App) | 1 | Flex Consumption |
| Microsoft.KeyVault/vaults | 1 | Standard |
| Microsoft.AppConfiguration/configurationStores | 1 | Free |
| Microsoft.OperationalInsights/workspaces | 1 | Pay-as-you-go |
| Microsoft.ManagedIdentity/userAssignedIdentities | 1 | Deploy identity |

**Status:**  All resources within limits. Storage: 0/250 used. All other resource types have no enforced subscription-level quota in eastus2.

---

## 7. Execution Checklist

### Phase 1: Planning
- [x] Analyze workspace
- [x] Gather requirements
- [x] Confirm subscription and location with user
- [x] Scan codebase
- [x] Select recipe (Bicep standalone)
- [x] Plan architecture
- [x] **User approved this plan**

### Phase 2: Execution
- [x] Generate Bicep modules (`infra/`)
- [x] Generate GitHub Actions workflow (`.github/workflows/`)
- [x] Generate bootstrap script (`infra/scripts/bootstrap.ps1`)
- [x] Generate parameter files
- [x]  Update plan status to "Ready for Validation"

### Phase 3: Validation
- [ ] Invoke azure-validate skill
- [ ] All validation checks pass
- [ ] Update plan status to "Validated"

### Phase 4: Deployment
- [ ] User runs bootstrap script (one-time)
- [ ] User triggers GitHub Actions workflow
- [ ] Update plan status to "Deployed"

---

## 8. Files to Generate

| Artifact | Path |
|----------|------|
| Main Bicep | `infra/main.bicep` |
| Parameters | `infra/main.bicepparam` |
| Container Apps Environment | `infra/modules/container-apps-environment.bicep` |
| Container App (generic) | `infra/modules/container-app.bicep` |
| SQL Server + DB | `infra/modules/sql-database.bicep` |
| Cosmos DB | `infra/modules/cosmos-db.bicep` |
| Service Bus | `infra/modules/service-bus.bicep` |
| Storage Accounts | `infra/modules/storage.bicep` |
| Key Vault | `infra/modules/key-vault.bicep` |
| App Configuration | `infra/modules/app-configuration.bicep` |
| Functions App | `infra/modules/functions.bicep` |
| Log Analytics | `infra/modules/log-analytics.bicep` |
| Managed Identity (deploy) | `infra/modules/deploy-identity.bicep` |
| RBAC Assignments | `infra/modules/role-assignments.bicep` |
| Bootstrap Script | `infra/scripts/bootstrap.ps1` |
| CI/CD Workflow | `.github/workflows/deploy.yml` |
