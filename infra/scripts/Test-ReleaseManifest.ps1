[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Path
)

$ErrorActionPreference = 'Stop'
$manifest = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json

if ($manifest.schemaVersion -ne 1) {
    throw 'Release manifest schemaVersion must be 1.'
}

if ($manifest.commitSha -notmatch '^[0-9a-f]{40}$') {
    throw 'Release manifest commitSha must be a full lowercase Git commit SHA.'
}

$expectedImages = 'gateway', 'api', 'scheduler', 'migrator', 'blazor'
foreach ($name in $expectedImages) {
    $reference = $manifest.images.$name
    if ($reference -notmatch '^ghcr\.io/.+@sha256:[0-9a-f]{64}$') {
        throw "Release manifest image '$name' must be an immutable GHCR digest reference."
    }
}

foreach ($artifactName in 'functions', 'uno') {
    $artifact = $manifest.artifacts.$artifactName
    if (-not $artifact.id -or [long]$artifact.id -le 0) {
        throw "Release manifest artifact '$artifactName' must have a positive immutable artifact ID."
    }
    if ($artifact.digest -notmatch '^[0-9a-f]{64}$') {
        throw "Release manifest artifact '$artifactName' must match actions/upload-artifact's 64-character SHA-256 digest output."
    }
}

if ($null -ne $manifest.previousManifestArtifactId -and [long]$manifest.previousManifestArtifactId -le 0) {
    throw 'previousManifestArtifactId must be null or a positive artifact ID.'
}

Write-Output "Release manifest '$Path' is valid."
