[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^https://')]
    [string] $GatewayUrl,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string] $CommitSha
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$gateway = $GatewayUrl.TrimEnd('/')
$createdId = $null

$health = Invoke-WebRequest -Uri "$gateway/health/full" -Method Get -TimeoutSec 60
if ($health.StatusCode -ne 200) {
    throw "Public aggregate health returned HTTP $($health.StatusCode)."
}

try {
    $payload = @{
        item = @{
            title = "deployment-smoke-$($CommitSha.Substring(0, 12))"
            priority = 'Medium'
        }
    } | ConvertTo-Json -Depth 4

    $created = Invoke-RestMethod `
        -Uri "$gateway/api/v1/task-items" `
        -Method Post `
        -ContentType 'application/json' `
        -Body $payload `
        -TimeoutSec 60

    $createdId = $created.item.id
    if (-not $createdId) {
        throw 'Functional smoke create response did not contain item.id.'
    }

    $loaded = Invoke-RestMethod `
        -Uri "$gateway/api/v1/task-items/$createdId" `
        -Method Get `
        -TimeoutSec 60
    if ($loaded.item.id -ne $createdId) {
        throw "Functional smoke read returned '$($loaded.item.id)' instead of '$createdId'."
    }
}
finally {
    if ($createdId) {
        Invoke-WebRequest `
            -Uri "$gateway/api/v1/task-items/$createdId" `
            -Method Delete `
            -TimeoutSec 60 | Out-Null
    }
}

Write-Output "Deployment health and create/read/delete smoke passed for $CommitSha."
