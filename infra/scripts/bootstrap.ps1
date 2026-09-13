<#
.SYNOPSIS
    One-time bootstrap: creates the deploy identity and narrow subscription deployment role,
    then adds a federated credential for GitHub Actions OIDC.

.DESCRIPTION
    Run this from a developer workstation with Owner/Contributor + User Access Administrator on the
    subscription. The script is rerunnable and never deploys application infrastructure or images.
    After this, all application deployments happen via GitHub Actions.

.PARAMETER SubscriptionId
    Azure subscription ID.

.PARAMETER Location
    Azure region. Default: eastus2.

.PARAMETER GitHubRepo
    GitHub repo in 'owner/repo' format. Used for federated credential subject.

.PARAMETER GitHubEnvironment
    GitHub Actions environment for the federated credential. Default: dev. The deploy workflow
    must use the same environment name.

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
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$GitHubRepo,

    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,254}$')]
    [string]$GitHubEnvironment = 'dev',

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
Write-Host "GitHub:       $GitHubRepo (environment: $GitHubEnvironment)"
Write-Host ""

# 1. Set subscription
Write-Host "[1/4] Setting subscription..." -ForegroundColor Yellow
az account set --subscription $SubscriptionId
if ($LASTEXITCODE -ne 0) { throw "Failed to set subscription" }

# 2. A human caller establishes the deploy identity and its narrow subscription-scope permission
# before GitHub can federate as that identity. The custom role permits ARM deployments plus
# resource-group read/write only; main.bicep keeps resource and RBAC management scoped to the RG.
Write-Host "[2/4] Establishing deploy identity and subscription deployment access..." -ForegroundColor Yellow
$foundationOutput = az deployment sub create `
    --location $Location `
    --template-file "$PSScriptRoot/../bootstrap/deploy-identity-foundation.bicep" `
    --parameters resourcePrefix=$ResourcePrefix `
                 environmentName=$EnvironmentName `
                 location=$Location `
    --query "properties.outputs" `
    --output json

if ($LASTEXITCODE -ne 0) { throw "Deploy identity foundation failed" }

$foundationOutputs = $foundationOutput | ConvertFrom-Json
$deployClientId = $foundationOutputs.deployIdentityClientId.value
$identityName = $foundationOutputs.deployIdentityName.value

Write-Host "  Deploy Identity Client ID: $deployClientId"

# Get tenant ID
$tenantId = (az account show --query tenantId -o tsv)
if ($LASTEXITCODE -ne 0) { throw "Failed to get tenant ID" }

# 3. Add federated credential only after the subscription deployment assignment exists
Write-Host "[3/4] Creating federated credential for GitHub Actions..." -ForegroundColor Yellow

$credentialName = "github-actions-$GitHubEnvironment"
$credentialList = az identity federated-credential list `
    --identity-name $identityName `
    --resource-group $rgName `
    --output json
if ($LASTEXITCODE -ne 0) { throw "Failed to list federated credentials" }

$credentialExists = @($credentialList | ConvertFrom-Json | Where-Object name -EQ $credentialName).Count -eq 1
$federatedCredentialArguments = @(
    '--name', $credentialName,
    '--identity-name', $identityName,
    '--resource-group', $rgName,
    '--issuer', 'https://token.actions.githubusercontent.com',
    '--subject', "repo:${GitHubRepo}:environment:$GitHubEnvironment",
    '--audiences', 'api://AzureADTokenExchange',
    '--output', 'none'
)

if ($credentialExists) {
    az identity federated-credential update @federatedCredentialArguments
}
else {
    az identity federated-credential create @federatedCredentialArguments
}

if ($LASTEXITCODE -ne 0) { throw "Failed to create or update federated credential" }

# 4. Output GitHub Actions variables
Write-Host ""
Write-Host "[4/4] Configure these as GitHub Actions variables (Settings > Secrets and variables > Actions > Variables):" -ForegroundColor Green
Write-Host "  AZURE_CLIENT_ID       = $deployClientId"
Write-Host "  AZURE_TENANT_ID       = $tenantId"
Write-Host "  AZURE_SUBSCRIPTION_ID = $SubscriptionId"
Write-Host ""

# Summary
Write-Host "=== Bootstrap Complete ===" -ForegroundColor Cyan
Write-Host "Resource Group:  $rgName"
Write-Host ""
Write-Host "Next: configure the variables above, then run the deploy workflow against the '$GitHubEnvironment' GitHub environment." -ForegroundColor Yellow
