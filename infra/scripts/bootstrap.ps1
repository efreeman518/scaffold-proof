<#
.SYNOPSIS
    One-time bootstrap: creates the resource group, deploys infrastructure (including
    the deploy managed identity), then adds a federated credential for GitHub Actions OIDC.

.DESCRIPTION
    Run this ONCE from a developer workstation with Owner/Contributor + User Access Administrator
    on the subscription. After this, all subsequent deployments happen via GitHub Actions.

.PARAMETER SubscriptionId
    Azure subscription ID.

.PARAMETER Location
    Azure region. Default: eastus2.

.PARAMETER GitHubRepo
    GitHub repo in 'owner/repo' format. Used for federated credential subject.

.PARAMETER GitHubBranch
    Branch name for federated credential. Default: main.

.PARAMETER ResourcePrefix
    Resource naming prefix. Default: taskflow.

.PARAMETER EnvironmentName
    Environment name. Default: dev.

.EXAMPLE
    ./bootstrap.ps1 -SubscriptionId "db98b283-631e-4f24-bd77-321332820725" `
                     -GitHubRepo "efreeman518/scaffold-proof"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SubscriptionId,

    [string]$Location = 'eastus2',

    [Parameter(Mandatory)]
    [string]$GitHubRepo,

    [string]$GitHubBranch = 'main',

    [string]$ResourcePrefix = 'taskflow',

    [string]$EnvironmentName = 'dev'
)

$ErrorActionPreference = 'Stop'
$prefix = "$ResourcePrefix-$EnvironmentName"
$rgName = "$prefix-rg"

Write-Host "=== TaskFlow Bootstrap ===" -ForegroundColor Cyan
Write-Host "Subscription: $SubscriptionId"
Write-Host "Location:     $Location"
Write-Host "RG:           $rgName"
Write-Host "GitHub:       $GitHubRepo (branch: $GitHubBranch)"
Write-Host ""

# 1. Set subscription
Write-Host "[1/6] Setting subscription..." -ForegroundColor Yellow
az account set --subscription $SubscriptionId
if ($LASTEXITCODE -ne 0) { throw "Failed to set subscription" }

# 2. Get current user for SQL Entra admin
Write-Host "[2/6] Getting signed-in user for SQL admin..." -ForegroundColor Yellow
$userInfo = az ad signed-in-user show --query "{id:id, name:displayName}" -o json | ConvertFrom-Json
$sqlAdminId = $userInfo.id
$sqlAdminName = $userInfo.name
Write-Host "  SQL Admin: $sqlAdminName ($sqlAdminId)"

# 3. Deploy infrastructure (creates RG + all resources including deploy identity)
Write-Host "[3/6] Deploying infrastructure (this takes 5-10 minutes)..." -ForegroundColor Yellow
$deployOutput = az deployment sub create `
    --location $Location `
    --template-file "$PSScriptRoot/../main.bicep" `
    --parameters resourcePrefix=$ResourcePrefix `
                 environmentName=$EnvironmentName `
                 location=$Location `
                 sqlAdminPrincipalId=$sqlAdminId `
                 sqlAdminPrincipalName=$sqlAdminName `
                 sqlAdminPrincipalType=User `
    --query "properties.outputs" `
    --output json

if ($LASTEXITCODE -ne 0) { throw "Infrastructure deployment failed" }

$outputs = $deployOutput | ConvertFrom-Json
$deployClientId = $outputs.deployIdentityClientId.value
$deployPrincipalId = $outputs.deployIdentityPrincipalId.value
$identityName = "$prefix-deploy-id"

Write-Host "  Deploy Identity Client ID: $deployClientId"

# 4. Get tenant ID
$tenantId = (az account show --query tenantId -o tsv)

# 5. Add federated credential for GitHub Actions
Write-Host "[4/6] Creating federated credential for GitHub Actions..." -ForegroundColor Yellow

$fedCredBody = @{
    name = "github-actions-$EnvironmentName"
    issuer = "https://token.actions.githubusercontent.com"
    subject = "repo:${GitHubRepo}:ref:refs/heads/$GitHubBranch"
    audiences = @("api://AzureADTokenExchange")
} | ConvertTo-Json -Compress

# Create federated credential on the managed identity
az identity federated-credential create `
    --name "github-actions-$EnvironmentName" `
    --identity-name $identityName `
    --resource-group $rgName `
    --issuer "https://token.actions.githubusercontent.com" `
    --subject "repo:${GitHubRepo}:ref:refs/heads/$GitHubBranch" `
    --audiences "api://AzureADTokenExchange"

if ($LASTEXITCODE -ne 0) { throw "Failed to create federated credential" }

# 6. Output GitHub Actions variables
Write-Host ""
Write-Host "[5/6] Configure these as GitHub Actions variables (Settings > Secrets and variables > Actions > Variables):" -ForegroundColor Green
Write-Host "  AZURE_CLIENT_ID       = $deployClientId"
Write-Host "  AZURE_TENANT_ID       = $tenantId"
Write-Host "  AZURE_SUBSCRIPTION_ID = $SubscriptionId"
Write-Host ""
Write-Host "[6/6] Configure these as GitHub Actions secrets (Settings > Secrets and variables > Actions > Secrets):" -ForegroundColor Green
Write-Host "  SQL_ADMIN_PRINCIPAL_ID   = $sqlAdminId"
Write-Host "  SQL_ADMIN_PRINCIPAL_NAME = $sqlAdminName"
Write-Host ""

# Summary
Write-Host "=== Bootstrap Complete ===" -ForegroundColor Cyan
Write-Host "Resource Group:  $rgName"
Write-Host "Gateway URL:     https://$($outputs.gatewayFqdn.value)"
Write-Host "Blazor URL:      https://$($outputs.blazorFqdn.value)"
Write-Host "Uno SWA URL:     https://$($outputs.staticWebAppDefaultHostname.value)"
Write-Host "Function App:    $($outputs.functionAppName.value)"
Write-Host ""
Write-Host "Next: push to '$GitHubBranch' branch to trigger CI/CD deployment." -ForegroundColor Yellow
