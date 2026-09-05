#requires -Version 7.0
<#
.SYNOPSIS
    Seeds a TaskFlow database with bulk task rows for the scale lanes.

.DESCRIPTION
    Runs MillionRowTaskFixture against an existing, already-migrated database. This is a manual tool, not part
    of any automated lane: a million rows takes minutes and gigabytes, and the integration tests use the same
    fixture at a far smaller count. Point it at a scratch database - it only inserts, and it never cleans up.

    The generated mix is documented on MillionRowTaskFixture: tenant-skewed, ~8% overdue, ~3% recurring
    templates, ~5% cancelled past the stale-cleanup window.

.PARAMETER ConnectionString
    Target database. Must already have the TaskFlow migrations applied (run TaskFlow.DatabaseMigrator first).

.PARAMETER Provider
    SqlServer or PostgreSql. Must match the target database.

.PARAMETER RowCount
    Rows to insert. Default 1,000,000.

.PARAMETER TenantCount
    Tenants to spread rows across, skewed toward the first. Default 5.

.EXAMPLE
    ./seed-million-rows.ps1 -ConnectionString "Server=localhost;Database=taskflow_scale;..." -Provider SqlServer

.EXAMPLE
    ./seed-million-rows.ps1 -ConnectionString "Host=localhost;Database=taskflow_scale;..." -Provider PostgreSql -RowCount 50000
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ConnectionString,

    [ValidateSet('SqlServer', 'PostgreSql')]
    [string]$Provider = 'SqlServer',

    [ValidateRange(1, 100000000)]
    [int]$RowCount = 1000000,

    [ValidateRange(1, 1000)]
    [int]$TenantCount = 5,

    [ValidateRange(100, 100000)]
    [int]$BatchSize = 5000
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')
$runner = Join-Path $repoRoot 'tests' 'Test.Support' 'Fixtures' 'SeedRunner'

if (-not (Test-Path $runner)) {
    Write-Host 'This script drives MillionRowTaskFixture from a throwaway console project.' -ForegroundColor Yellow
    Write-Host 'No SeedRunner project is committed: the fixture is exercised by the integration lane at a' -ForegroundColor Yellow
    Write-Host 'reduced row count, and a committed runner would be a second build target to keep green for' -ForegroundColor Yellow
    Write-Host 'a manual task. Create one when you need it:' -ForegroundColor Yellow
    Write-Host ''
    Write-Host "  dotnet new console -o $runner" -ForegroundColor Cyan
    Write-Host "  dotnet add $runner reference tests/Test.Support/Test.Support.csproj" -ForegroundColor Cyan
    Write-Host ''
    Write-Host 'Program.cs body:' -ForegroundColor Yellow
    Write-Host ''
    Write-Host '  var options = new DbContextOptionsBuilder<TaskFlowDbContextTrxn>()' -ForegroundColor Cyan
    Write-Host '      .UseTaskFlowProvider(new TaskFlowProviderOptions(provider, connectionString,' -ForegroundColor Cyan
    Write-Host '          TaskFlowDbContextBase.MigrationHistoryTable, TaskFlowDbContextBase.SchemaName))' -ForegroundColor Cyan
    Write-Host '      .UseColumnEncryption(TestColumnEncryption.Encryptor)' -ForegroundColor Cyan
    Write-Host '      .AddInterceptors(new VersionTimestampInterceptor(),' -ForegroundColor Cyan
    Write-Host '          new BlindIndexInterceptor(TestColumnEncryption.Keys.BlindIndexKey)).Options;' -ForegroundColor Cyan
    Write-Host '  await using var db = new TaskFlowDbContextTrxn(options) { AuditId = "seed" };' -ForegroundColor Cyan
    Write-Host '  var fixture = new MillionRowTaskFixture(tenantIds, DateTimeOffset.UtcNow);' -ForegroundColor Cyan
    Write-Host '  await fixture.SeedAsync(db, rowCount, batchSize);' -ForegroundColor Cyan
    Write-Host ''
    Write-Host "Then re-run this script with the same arguments." -ForegroundColor Yellow
    exit 2
}

Write-Host "Seeding $RowCount rows across $TenantCount tenants ($Provider), batch size $BatchSize." -ForegroundColor Green
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

& dotnet run --project $runner --configuration Release -- `
    --connection-string $ConnectionString `
    --provider $Provider `
    --row-count $RowCount `
    --tenant-count $TenantCount `
    --batch-size $BatchSize

if ($LASTEXITCODE -ne 0) {
    throw "Seed runner failed with exit code $LASTEXITCODE."
}

$stopwatch.Stop()
Write-Host "Seeded $RowCount rows in $($stopwatch.Elapsed.ToString('hh\:mm\:ss'))." -ForegroundColor Green
