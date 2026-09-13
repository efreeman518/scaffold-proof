# TaskFlow Infrastructure

Azure infrastructure for the TaskFlow dev environment. All resources deploy to a single resource group using Bicep, with CI/CD via GitHub Actions and OIDC (no stored credentials).

## Architecture

| Resource | Dev SKU | Prod SKU | Purpose |
|----------|---------|----------|---------|
| Container Apps Environment | Consumption | Consumption | Hosts Gateway, API, Scheduler, Blazor |
| Database | SQL Basic (5 DTU) | SQL Hyperscale `HS_Gen5_2`, zone-redundant, 1 HA/read-scale replica | Azure lane transactional + query databases. |
| Cosmos DB | Serverless | Serverless | Read projections (`taskflow-db`/`task-views`, matching `TaskFlow.Api` appsettings) |
| Service Bus | Standard | Standard | Domain events (3 filtered subscriptions: `projection`, `ai-review`, `workflow`) + command queue |
| Azure Functions | Flex Consumption (FC1) | Flex Consumption (FC1) | Event-driven processing, `functionAppScaleLimit` param |
| Static Web Apps | Free | Free | React and Uno WASM frontends |
| Redis | Azure Managed Redis `Balanced_B0`, no HA | Azure Managed Redis `Balanced_B5`+, HA | FusionCache L2 (`ConnectionStrings__Redis1`), API + Scheduler |
| Storage Accounts | Standard LRS (x2) | Standard LRS (x2) | App blobs/tables/queues + Functions runtime |
| Key Vault | Standard | Standard | Secrets management |
| App Configuration | Free | Free | Centralized config |
| Log Analytics | PerGB2018 (30d) | PerGB2018 (30d) | Logging + Application Insights |
| User-Assigned Identities | - | - | GitHub Actions OIDC and SQL deployment admin, migration DDL, runtime DML |

Azure resource access uses **managed identities and Entra authentication** where supported. Functions storage and Redis still require access keys; Redis reaches API and Scheduler only through Container Apps secret references, never plain environment values. End-user application auth is separate: this reference deployment defaults to `AuthMode: Scaffold`, supplies an automatic principal, and does not require a login.

### Container Apps scale profiles

Gateway, API, Scheduler, and Blazor each take a `<host>Profile` object param (`minReplicas`, `maxReplicas`, `concurrentRequests`, `cpu`, `memory`). `main.dev.bicepparam` keeps public/request-driven hosts at scale-to-zero with small ceilings and no HTTP concurrency rule; Scheduler stays at one replica because its embedded TickerQ service has no ingress or event scale rule. `main.prod.bicepparam` sets Gateway/API to min 2 / max 100 / 50 concurrent requests, Blazor to min 2 / max 30, and Scheduler to two always-on replicas (min 2 / max 2).

### Azure lane contract

`infra/main.bicep` is Azure-only under D-060. Every relevant host receives `Hosting__Lane=Azure` plus SQL Server,
Service Bus, Azure Blob, Cosmos, Azure Table, and Blob Data Protection settings. Azure AI Search remains selectable
through `searchProvider`; SQL is the non-AI fallback. PostgreSQL, RabbitMQ, S3, and MongoDB belong to the separate
NonAzure Compose lane and are not Azure Bicep alternatives.

### Connection strings

Every emitted SQL connection string carries an explicit `Max Pool Size`. `ConnectionStrings__TaskFlowDbContextQuery`
(API, Scheduler, Functions) resolves to the read/replica connection string; all other contexts share the primary
read-write string. SqlClient selects an explicit user-assigned identity by client ID. The migration identity receives
`db_ddladmin`, `db_datareader`, and `db_datawriter`; the shared runtime SQL identity receives DML only on the
`taskflow`, `flowengine`, and `scheduler` schemas. The federated deploy identity is SQL Entra administrator and runs
the idempotent principal bootstrap before migrations. Users are bound by explicit client-ID SID, so SQL needs no
tenant-wide Directory Readers permission.

## Prerequisites

- **Azure CLI** >= 2.60 with Bicep CLI
- **PowerShell** 7+
- **Azure subscription** with Owner or Contributor + User Access Administrator. Bootstrap needs
  `Microsoft.Authorization/roleDefinitions/write` and `roleAssignments/write` to create its narrow custom role.
- **GitHub repo** with Actions enabled
- Signed in to Azure CLI: `az login`

## Authentication Posture

TaskFlow is a compiled scaffold proof, not a provisioned identity sample. Its supported default is `AuthMode: Scaffold`; API and UI flows run with the fixed scaffold principal and no login screen. Do not expose that mode as a production security boundary.

### Optional Live Interactive Identity

Live Entra ID or Entra External ID is deployment-only. Before switching any deployed client away from scaffold auth:

1. Create separate app registrations when the API, public client, and admin portal have different redirect URIs, roles, or operators. Store client IDs in deployment configuration, not source placeholders.
2. Register exact public HTTPS redirect and post-logout URIs, including any path base and callback path. Never register an internal container host as the production callback.
3. Define required app roles, create the enterprise application/service principal, require assignment where appropriate, and assign the operator user or group.
4. Add delegated `openid` and `profile` permissions and grant tenant admin consent before testing. CIAM tenants commonly disable user consent.
5. For Entra External ID user flows, use `https://<tenant-subdomain>.ciamlogin.com/`. Do not use `login.microsoftonline.com` as the interactive CIAM authority.
6. Create a local CIAM user for acceptance and assign its role. Guest or personal Microsoft account administrators generally cannot sign in through CIAM local-user flows.
7. If automation creates the service principal, add the `WindowsAzureActiveDirectoryIntegratedApp` tag when operators need it visible under the portal's default Enterprise Applications filter.
8. Implement the selected client flow, disable scaffold auth in that deployment, then complete one real role-bearing sign-in per enabled UI head from its published `Release` output. Verify the resulting token reaches the Gateway. Automated scaffold tests are not equivalent evidence.

Secrets belong in Key Vault or the deployment secret store. Never commit client secrets.

## Step 1: Run Bootstrap

The bootstrap script is a rerunnable prerequisite operation from your local machine. It creates only the resource group,
deploy identity, subscription deployment permission, and GitHub Actions OIDC trust. It never deploys application
infrastructure or placeholder images; the workflow owns the first and every later application deployment.

```powershell
cd infra/scripts

./bootstrap.ps1 `
    -SubscriptionId "db98b283-631e-4f24-bd77-321332820725" `
    -GitHubRepo "efreeman518/scaffold-proof"
```

Optional parameters (shown with defaults):

| Parameter | Default | Description |
|-----------|---------|-------------|
| `-Location` | `eastus2` | Azure region |
| `-ResourcePrefix` | `taskflow` | Naming prefix for all resources |
| `-EnvironmentName` | `dev` | Environment suffix |
| `-GitHubEnvironment` | `dev` | GitHub environment used by deploy jobs. Safe characters: letters, digits, `.`, `_`, `-` |

The script will:

1. Set the active subscription
2. Deploy `bootstrap/deploy-identity-foundation.bicep` as the signed-in human. It creates the resource group, deploy UAMI,
   and a custom subscription role limited to `Microsoft.Resources/deployments/*` plus resource-group read/write.
3. Create or update the environment-bound federated credential on the deploy UAMI.
4. Print the exact values needed for GitHub configuration (Step 2).

The current workflow jobs use the `dev` GitHub environment, so the default credential subject is
`repo:owner/repo:environment:dev`. If the workflow environment changes, rerun bootstrap with the same
`-GitHubEnvironment` value. GitHub documents this environment subject format in its
[OIDC reference](https://docs.github.com/en/actions/reference/security/oidc#example-subject-claims).

The subscription role is intentionally not Contributor. It lets the UAMI submit the
[subscription-scoped Bicep deployment](https://learn.microsoft.com/en-us/azure/azure-resource-manager/bicep/deploy-to-subscription)
and create or update resource groups, but underlying application resources and RBAC still require the existing
TaskFlow resource-group assignments. See Microsoft's [custom role guidance](https://learn.microsoft.com/en-us/azure/role-based-access-control/custom-roles).

The output block contains the GitHub variables needed for the next step.

## Step 2: Configure GitHub Repository

After bootstrap completes, configure your GitHub repo at **Settings -> Secrets and variables -> Actions**.

### Variables (Settings -> Variables -> New repository variable)

| Variable | Value | Source |
|----------|-------|--------|
| `AZURE_CLIENT_ID` | *(from bootstrap output)* | Deploy managed identity client ID |
| `AZURE_TENANT_ID` | *(from bootstrap output)* | Entra tenant ID |
| `AZURE_SUBSCRIPTION_ID` | `db98b283-631e-4f24-bd77-321332820725` | Subscription ID |

### Private NuGet Feed (`NUGET_PAT`)

The solution references private packages (`EF.*`) from the `efreeman518-github` GitHub Packages feed (configured in `nuget.config`). The build agent needs a PAT to authenticate.

| Secret | Value |
|--------|-------|
| `NUGET_PAT` | Package read token supplied by the CI secret store |

The workflow injects this secret without modifying `nuget.config`:

1. **Container restores** - passed as a BuildKit secret and exposed only to the restore process through `NuGetPackageSourceCredentials_efreeman518-github`.
2. **Functions and Uno publishes** - exposed to NuGet through the same process environment convention.

The credential is never passed as a Docker build argument, written into a NuGet config file, or included in an image layer or deployment artifact.

To create the PAT: **GitHub -> Settings -> Developer settings -> Personal access tokens -> Fine-grained tokens** -> grant `read:packages` on the `efreeman518` account.

> `NUGET_PAT` is required. The release build fails at entry when it is absent.

## Step 3: Trigger Deployment

The deploy workflow currently runs manually through `workflow_dispatch`. Supply `operation=deploy` and a full green `main` commit SHA, or select `operation=rollback` to activate the recorded previous release. It also preserves a `workflow_call` interface for CI, but the caller in `ci.yml` remains disabled until Azure bootstrap and repository variables are configured.

For deploy, the workflow validates the exact green commit, builds each image and the Functions/React/Uno bundles once, records immutable digests and artifact IDs, provisions with the existing runtime images, creates the migration SQL principal, runs migrations, grants schema-scoped runtime SQL access, activates the recorded release, publishes Functions through Flex OneDeploy, verifies readiness and functional CRUD, then records current and previous release manifests. Rollback downloads the recorded prior artifacts and images without rebuilding or reversing database migrations.

The Function App follows Microsoft's [Flex Consumption IaC contract](https://learn.microsoft.com/en-us/azure/azure-functions/functions-infrastructure-as-code): an existing deployment container, managed-identity deployment storage, explicit .NET 10 isolated runtime, and `scaleAndConcurrency`. Its Blob trigger follows the documented [identity-based binding roles](https://learn.microsoft.com/en-us/azure/azure-functions/manage-connections), including Storage Blob Data Owner and Storage Queue Data Contributor.

The repository is public and images are published by its workflow with `GITHUB_TOKEN`; GitHub applies the repository's
visibility model to newly created packages, so Container Apps can pull them anonymously. This deployment does not invent
or store GHCR pull credentials. If package visibility is later made private, registry authentication must be designed and
provisioned before deployment.

## CI/CD Pipeline Flow

```
validate exact green SHA
  -> build immutable images and bundles once
  -> provision infrastructure with current runtime images
  -> provision migration SQL identity
  -> run database migrations
  -> grant schema-scoped runtime SQL access
  -> activate digest-pinned runtime and publish recorded Functions through OneDeploy
  -> check API database readiness and public health
  -> create/read/delete functional smoke with cleanup
  -> record current and previous release manifests
```

## File Structure

```
infra/
--- main.bicep              # Orchestration (subscription-scoped)
--- main.bicepparam         # Parameter defaults (dev)
--- main.dev.bicepparam     # Explicit dev profile
--- main.prod.bicepparam    # Prod profile (Hyperscale, HA, scale rules)
--- bootstrap/
-   --- deploy-identity-foundation.bicep # Human-run deploy identity and narrow subscription role
--- modules/
-   --- app-configuration.bicep
-   --- container-app.bicep
-   --- container-apps-environment.bicep
-   --- cosmos-db.bicep
-   --- cosmos-rbac.bicep
-   --- deploy-identity.bicep
-   --- functions.bicep
-   --- key-vault.bicep
-   --- log-analytics.bicep
-   --- redis.bicep
-   --- redis-rbac.bicep
-   --- role-assignment.bicep
-   --- service-bus.bicep
-   --- sql-database.bicep
-   --- static-web-app.bicep
-   --- storage.bicep
--- scripts/
-   --- bootstrap.ps1       # One-time setup script
-   --- Set-AzureSqlPrincipal.ps1 # Idempotent migration/runtime SQL grants
-   --- Invoke-DeploymentSmoke.ps1 # Post-deploy health and CRUD smoke
-   --- Test-ReleaseManifest.ps1   # Immutable release manifest validation
--- README.md               # This file
```

## Post-Deployment URLs

After successful deployment, access the app at:

| Service | URL |
|---------|-----|
| Gateway | `https://taskflow-dev-gateway.<region>.azurecontainerapps.io` |
| Blazor UI | `https://taskflow-dev-blazor.<region>.azurecontainerapps.io` |
| React UI | `https://<auto-generated>.azurestaticapps.net` |
| Uno WASM UI | `https://<auto-generated>.azurestaticapps.net` |
| API (internal) | `https://taskflow-dev-api.<region>.azurecontainerapps.io` |

Exact URLs are visible in the deploy workflow summary.

## Redeploying Infrastructure Only

Run the manual deploy workflow with a full green `main` commit SHA. Bootstrap is safe to rerun, but it intentionally
repairs only the identity foundation and never redeploys application infrastructure.

## Troubleshooting

| Problem | Fix |
|---------|-----|
| `AADSTS700016` on deploy | Verify `AZURE_CLIENT_ID` matches the deploy identity's client ID |
| `FederatedIdentityCredential` error | Check the credential subject matches `repo:owner/repo:environment:dev` and the job uses that GitHub environment |
| Subscription deployment authorization fails | Rerun bootstrap as subscription Owner or Contributor + User Access Administrator so the custom deployment role and assignment exist before OIDC is used |
| SQL principal provisioning fails | Verify `AZURE_CLIENT_ID` is the Bicep-created deploy identity and remains SQL Entra administrator |
| Container image pull fails | Verify GHCR packages still inherit this public repository's visibility; private registry authentication is not configured |
| Functions deploy fails | Verify the `function-releases` container, Function App Storage Blob Data Owner role, and Flex OneDeploy output |
| SWA deploy fails | Check that `swa-name` output is correctly passed from deploy-infra job |

> NonAzure lane (Docker Compose on a VPS): see [`deploy/compose/README.md`](../deploy/compose/README.md).
