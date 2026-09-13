[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Migration', 'Runtime')]
    [string]$AccessProfile,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9.-]+\.database\.windows\.net$')]
    [string]$ServerName,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$')]
    [string]$DatabaseName,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9-]{0,127}$')]
    [string]$IdentityName,

    [Parameter(Mandatory)]
    [guid]$IdentityClientId
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Supplying the client ID as the contained user's SID avoids a Microsoft Graph lookup. This keeps
# first-deploy automation independent of tenant-wide Directory Readers while binding SQL to the exact UAMI.
$clientId = $IdentityClientId.ToString('D')
$createUser = @"
DECLARE @identityName sysname = N'$IdentityName';
DECLARE @clientId uniqueidentifier = '$clientId';
DECLARE @expectedSid varbinary(16) = CONVERT(varbinary(16), @clientId);
DECLARE @actualSid varbinary(85) = (SELECT [sid] FROM sys.database_principals WHERE [name] = @identityName);

IF @actualSid IS NOT NULL AND @actualSid <> @expectedSid
    THROW 51000, 'Existing database principal name is bound to a different identity.', 1;

IF @actualSid IS NULL
BEGIN
    DECLARE @createUserSql nvarchar(max) = N'CREATE USER ' + QUOTENAME(@identityName)
        + N' WITH SID = ' + CONVERT(varchar(34), @expectedSid, 1) + N', TYPE = E;';
    EXEC (@createUserSql);
END;
"@

$query = if ($AccessProfile -eq 'Migration') {
    $createUser + @"

IF IS_ROLEMEMBER(N'db_datareader', N'$IdentityName') <> 1
    ALTER ROLE [db_datareader] ADD MEMBER [$IdentityName];
IF IS_ROLEMEMBER(N'db_datawriter', N'$IdentityName') <> 1
    ALTER ROLE [db_datawriter] ADD MEMBER [$IdentityName];
IF IS_ROLEMEMBER(N'db_ddladmin', N'$IdentityName') <> 1
    ALTER ROLE [db_ddladmin] ADD MEMBER [$IdentityName];
"@
}
else {
    $createUser + @"

IF DATABASE_PRINCIPAL_ID(N'taskflow_runtime') IS NULL
    CREATE ROLE [taskflow_runtime];
IF IS_ROLEMEMBER(N'taskflow_runtime', N'$IdentityName') <> 1
    ALTER ROLE [taskflow_runtime] ADD MEMBER [$IdentityName];

IF SCHEMA_ID(N'taskflow') IS NULL OR SCHEMA_ID(N'flowengine') IS NULL OR SCHEMA_ID(N'scheduler') IS NULL
    THROW 51001, 'Runtime schemas are missing; migrations must finish before runtime grants.', 1;

GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[taskflow] TO [taskflow_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[flowengine] TO [taskflow_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[scheduler] TO [taskflow_runtime];
"@
}

$accessToken = az account get-access-token `
    --resource 'https://database.windows.net/' `
    --query accessToken `
    --output tsv
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($accessToken)) {
    throw 'Failed to acquire the Azure SQL access token.'
}

$connectionString = "Server=tcp:$ServerName,1433;Database=$DatabaseName;Encrypt=True;TrustServerCertificate=False;Connect Timeout=30;"
Invoke-Sqlcmd `
    -ConnectionString $connectionString `
    -AccessToken $accessToken `
    -Query $query `
    -QueryTimeout 60 `
    -AbortOnError `
    -ErrorLevel 11 `
    -OutputSqlErrors $true

Write-Host "Azure SQL $AccessProfile access is ready for $IdentityName."
