# TaskFlow Infrastructure

Azure infrastructure for the TaskFlow dev environment. All resources deploy to a single resource group using Bicep, with CI/CD via GitHub Actions and OIDC (no stored credentials).

## Architecture

| Resource | SKU | Purpose |
|----------|-----|---------|
| Container Apps Environment | Consumption | Hosts Gateway, API, Scheduler, Blazor |
| Azure SQL | Basic (5 DTU) | Transactional + query databases |
| Cosmos DB | Serverless | Read projections (TaskItemViews) |
| Service Bus | Standard | Domain events + command queue |
| Azure Functions | Flex Consumption (FC1) | Event-driven processing |
| Static Web App | Free | Uno WASM frontend |
| Redis Cache | Basic C0 | FusionCache L2 |
| Storage Accounts | Standard LRS (x2) | App blobs/tables + Functions runtime |
| Key Vault | Standard | Secrets management |
| App Configuration | Free | Centralized config |
| Log Analytics | PerGB2018 (30d) | Logging + Application Insights |
| User-Assigned Identity | - | GitHub Actions OIDC deploy identity |

Azure resource access uses **managed identities and Entra authentication** where supported (no shared application keys except Functions storage, which requires one). End-user application auth is separate: this reference deployment defaults to `AuthMode: Scaffold`, supplies an automatic principal, and does not require a login.

## Prerequisites

- **Azure CLI** >= 2.60 with Bicep CLI
- **PowerShell** 7+
- **Azure subscription** with Owner or Contributor + User Access Administrator
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

The bootstrap script is a **one-time** operation run from your local machine. It deploys all infrastructure and creates the federated credential for GitHub Actions OIDC.

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
| `-GitHubBranch` | `main` | Branch for OIDC federated credential |

The script will:

1. Set the active subscription
2. Look up your signed-in Entra user (used as SQL admin)
3. Deploy `main.bicep` at subscription scope (creates RG + all resources)
4. Create a federated credential on the deploy managed identity for GitHub Actions
5. Print the exact values you need for GitHub configuration (Step 2)

> **Deployment takes 5-10 minutes.** Watch for the output block at the end - it contains the values for the next step.

## Step 2: Configure GitHub Repository

After bootstrap completes, configure your GitHub repo at **Settings -> Secrets and variables -> Actions**.

### Variables (Settings -> Variables -> New repository variable)

| Variable | Value | Source |
|----------|-------|--------|
| `AZURE_CLIENT_ID` | *(from bootstrap output)* | Deploy managed identity client ID |
| `AZURE_TENANT_ID` | *(from bootstrap output)* | Entra tenant ID |
| `AZURE_SUBSCRIPTION_ID` | `db98b283-631e-4f24-bd77-321332820725` | Subscription ID |

### Secrets (Settings -> Secrets -> New repository secret)

| Secret | Value | Source |
|--------|-------|--------|
| `SQL_ADMIN_PRINCIPAL_ID` | *(from bootstrap output)* | Your Entra user object ID |
| `SQL_ADMIN_PRINCIPAL_NAME` | *(from bootstrap output)* | Your Entra user display name |

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

For deploy, the workflow validates the exact green commit, builds each image and Functions/Uno bundle once, records immutable digests and artifact IDs, provisions with the existing runtime images, runs migrations, activates the recorded release, verifies readiness and functional CRUD, then records current and previous release manifests. Rollback downloads the recorded prior artifacts and images without rebuilding or reversing database migrations.

## CI/CD Pipeline Flow

```
validate exact green SHA
  -> build immutable images and bundles once
  -> provision infrastructure with current runtime images
  -> run database migrations
  -> activate digest-pinned runtime and recorded bundles
  -> check API database readiness and public health
  -> create/read/delete functional smoke with cleanup
  -> record current and previous release manifests
```

## File Structure

```
infra/
--- main.bicep              # Orchestration (subscription-scoped)
--- main.bicepparam         # Parameter defaults
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
-   --- role-assignment.bicep
-   --- service-bus.bicep
-   --- sql-database.bicep
-   --- static-web-app.bicep
-   --- storage.bicep
--- scripts/
-   --- bootstrap.ps1       # One-time setup script
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
| Uno WASM UI | `https://<auto-generated>.azurestaticapps.net` |
| API (internal) | `https://taskflow-dev-api.<region>.azurecontainerapps.io` |

Exact URLs are printed by the bootstrap script and visible in the deploy workflow summary.

## Redeploying Infrastructure Only

To redeploy infra without pushing code, run the bootstrap script again. It's idempotent - existing resources update in place.

## Troubleshooting

| Problem | Fix |
|---------|-----|
| `AADSTS700016` on deploy | Verify `AZURE_CLIENT_ID` matches the deploy identity's client ID |
| `FederatedIdentityCredential` error | Check federated credential subject matches `repo:owner/repo:ref:refs/heads/main` |
| SQL deployment fails | Ensure `SQL_ADMIN_PRINCIPAL_ID` and `SQL_ADMIN_PRINCIPAL_NAME` secrets are set |
| Container image pull fails | Verify repo packages are public, or add `packages: read` permission |
| Functions deploy fails | Functions require `allowSharedKeyAccess: true` on their storage account (already configured) |
| SWA deploy fails | Check that `swa-name` output is correctly passed from deploy-infra job |
