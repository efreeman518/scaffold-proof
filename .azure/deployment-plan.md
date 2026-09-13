# Azure Deployment Plan

> **Status:** Ready for Validation

Generated: 2026-04-23

---

## 1. Project Overview

**Goal:** Deploy TaskFlow multi-tenant task management application to a single Azure dev environment. Gateway, API, Scheduler, and Blazor run in Azure Container Apps; Functions uses Flex Consumption and React/Uno use Static Web Apps. The deployment uses minimal SKUs, managed identities, App Configuration, and Key Vault.

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
| TaskFlow.React | Web UI | React | `src/UI/TaskFlow.React/` |
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
| TaskFlow.Scheduler | Container App | min 1 / max 1, 0.25 vCPU, 0.5Gi (always-on) | min 2 / max 2 (always-on, no concurrency rule), 0.5 vCPU, 1Gi | Internal only (no ingress) |
| TaskFlow.Functions | Functions Flex Consumption | `functionAppScaleLimit` 20 | `functionAppScaleLimit` 20 | Internal (Service Bus trigger) |
| TaskFlow.Blazor | Container App | min 0 / max 1, 0.25 vCPU, 0.5Gi | min 2 / max 30, 0.5 vCPU, 1Gi | External |
| TaskFlow.React | Static Web App | Free | Free | External |
| TaskFlow.Uno | Static Web App | Free | Free | External |

### Data & Messaging Service Mapping

| Service | Azure Resource | SKU/Tier | Est. $/mo |
|---------|---------------|----------|-----------|
| Database | Azure SQL Database | Dev: Basic DTU (5 DTU). Prod: SQL Hyperscale `HS_Gen5_2` zone-redundant + HA/read-scale replica | Dev ~$5; prod materially higher (Hyperscale/HA priced per vCore + replica) |
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
| User-Assigned Managed Identities | Deploy SQL admin, migration DDL, runtime DML | N/A | $0 |
| Container Registry | N/A - using ghcr.io | N/A | $0 |

**Total estimated: ~$17-27/mo**

### Identity & Access (Managed Identities + RBAC)

The baseline deployment keeps end-user `AuthMode: Scaffold` so the reference app remains runnable without a login or tenant provisioning. Managed identities below secure Azure resource access; they do not convert the scaffold principal into production user authentication. Live interactive Entra/CIAM setup is an explicit deployment hardening step documented in `infra/README.md` and must disable scaffold auth before public use.

| Identity | Assigned To | Roles |
|----------|------------|-------|
| System MI (Gateway) | Gateway Container App | App Configuration Data Reader, Key Vault Secrets User |
| System MI (API) | API Container App | Service Bus Data Sender, Storage Blob Data Contributor, Storage Table Data Contributor, Cosmos DB Data Contributor, App Configuration Data Reader, Key Vault Secrets User, Redis Entra access policy assignment |
| System MI (Scheduler) | Scheduler Container App | Service Bus Data Sender, Storage Blob Data Contributor, Storage Table Data Contributor, App Configuration Data Reader, Key Vault Secrets User, Redis Entra access policy assignment |
| System MI (Functions) | Functions App | Service Bus Data Receiver, Storage Blob Data Contributor, Storage Queue Data Contributor, Storage Table Data Contributor, Cosmos DB Data Contributor, App Configuration Data Reader, Key Vault Secrets User |
| User-Assigned MI (deploy) | GitHub Actions and SQL provisioning | Contributor and User Access Administrator (RG scope), SQL Entra administrator |
| User-Assigned MI (migration) | DatabaseMigrator job | `db_ddladmin`, `db_datareader`, `db_datawriter` |
| User-Assigned MI (runtime) | API, Scheduler, Functions | DML on `taskflow`, `flowengine`, and `scheduler` schemas only |

Redis access policy assignments grant the API/Scheduler managed identities Entra data-plane permission on the
default database, but `ConnectionStrings__Redis1` still authenticates with the access key today: StackExchange.Redis
needs the `Microsoft.Azure.StackExchangeRedis` token-provider package wired in application code to use the Entra
grant, which is out of scope for this infra-only change. Container Apps stores that key as a secret reference.
SQL identities are user-assigned so the deployment workflow can bind contained users directly to stable client-ID
SIDs without Microsoft Graph lookup. Runtime hosts keep separate system identities for their other Azure RBAC.

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
| Microsoft.App/containerApps | 4 | Gateway, API, Scheduler, Blazor |
| Microsoft.Web/staticSites | 2 | React and Uno WASM frontends |
| Microsoft.Sql/servers | 1 | Entra-only auth |
| Microsoft.Sql/servers/databases | 1 | Dev Basic DTU; prod Hyperscale HA replica |
| Microsoft.Cache/redisEnterprise + /databases | 1 + 1 | Azure Managed Redis (FusionCache L2) |
| Microsoft.DocumentDB/databaseAccounts | 1 | Serverless |
| Microsoft.ServiceBus/namespaces | 1 | Standard, 3 topic subscriptions (`projection`, `ai-review`, `workflow`) |
| Microsoft.Storage/storageAccounts | 2 | App data + Functions runtime |
| Microsoft.Web/sites (Function App) | 1 | Flex Consumption |
| Microsoft.KeyVault/vaults | 1 | Standard |
| Microsoft.AppConfiguration/configurationStores | 1 | Free |
| Microsoft.OperationalInsights/workspaces | 1 | Pay-as-you-go |
| Microsoft.ManagedIdentity/userAssignedIdentities | 3 | Deploy SQL administrator, migration DDL, runtime DML |

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
| RBAC Assignments | `infra/modules/role-assignment.bicep` |
| Bootstrap Script | `infra/scripts/bootstrap.ps1` |
| CI/CD Workflow | `.github/workflows/deploy.yml` |
