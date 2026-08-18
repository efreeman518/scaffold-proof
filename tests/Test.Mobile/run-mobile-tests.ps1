<#
.SYNOPSIS
Builds or verifies Android mobile test dependencies, then runs Test.Mobile.

.DESCRIPTION
Use from repo root for local Android Appium runs. MSTest stays a validator;
this script owns APK build, emulator readiness, Appium readiness, and mobile
lane enablement.

.EXAMPLE
powershell -NoProfile -File tests/Test.Mobile/run-mobile-tests.ps1

Builds the Android APK, starts or verifies the default AVD, starts or verifies
Appium, then runs all MobileUI tests.

.EXAMPLE
powershell -NoProfile -File tests/Test.Mobile/run-mobile-tests.ps1 -SkipBuild

Skips the Android APK rebuild, rebuilds the small test project, then runs
MobileUI against the existing APK.

.EXAMPLE
powershell -NoProfile -File tests/Test.Mobile/run-mobile-tests.ps1 -VisibleEmulator -AvdName Android_Emulator_35

Starts the named AVD with a visible emulator window instead of the default
headless window.

.EXAMPLE
powershell -NoProfile -File tests/Test.Mobile/run-mobile-tests.ps1 -SkipBuild -Filter "FullyQualifiedName~TaskFlowMobile_AppLaunches_AndRendersNativeSurface"

Runs only the launch smoke against an already-built APK.

.EXAMPLE
powershell -NoProfile -File tests/Test.Mobile/run-mobile-tests.ps1 -AndroidSdk "C:\Program Files (x86)\Android\android-sdk" -AppiumServerUrl "http://127.0.0.1:4723/"

Uses explicit Android SDK and Appium endpoint paths when environment variables
are not set.
#>
param(
    [string]$AvdName = $(if ($env:TASKFLOW_MOBILE_AVD_NAME) { $env:TASKFLOW_MOBILE_AVD_NAME } else { "Android_Emulator_35" }),
    [string]$AndroidSdk = $(if ($env:ANDROID_HOME) { $env:ANDROID_HOME } elseif ($env:ANDROID_SDK_ROOT) { $env:ANDROID_SDK_ROOT } else { Join-Path $env:LOCALAPPDATA "Android\Sdk" }),
    [string]$AppiumServerUrl = $(if ($env:TASKFLOW_APPIUM_SERVER_URL) { $env:TASKFLOW_APPIUM_SERVER_URL } else { "http://127.0.0.1:4723/" }),
    [string]$Filter = "TestCategory=MobileUI",
    [int]$BootTimeoutSeconds = 300,
    [int]$AppiumTimeoutSeconds = 60,
    [switch]$SkipBuild,
    [switch]$VisibleEmulator
)

$ErrorActionPreference = "Stop"

# Keep process orchestration outside MSTest. The test assembly should validate
# UI behavior only; this runner owns local Android/Appium readiness.
function Get-SourceRoot {
    $directory = Get-Item $PSScriptRoot
    while ($null -ne $directory) {
        if (Test-Path (Join-Path $directory.FullName "TaskFlow.slnx")) {
            return $directory.FullName
        }

        $directory = $directory.Parent
    }

    throw "Could not locate TaskFlow.slnx from $PSScriptRoot."
}

function Test-AppiumReady {
    param([string]$Url)

    try {
        $status = Invoke-RestMethod -Uri ([Uri]::new([Uri]$Url, "/status")) -TimeoutSec 3
        return $null -ne $status.value -and $status.value.ready -eq $true
    }
    catch {
        return $false
    }
}

function Wait-Until {
    param(
        [scriptblock]$Probe,
        [TimeSpan]$Timeout,
        [string]$FailureMessage
    )

    $deadline = (Get-Date).Add($Timeout)
    do {
        if (& $Probe) {
            return
        }

        Start-Sleep -Seconds 2
    }
    while ((Get-Date) -lt $deadline)

    throw $FailureMessage
}

$repoRoot = Get-SourceRoot
$testProject = Join-Path $repoRoot "tests\Test.Mobile\Test.Mobile.csproj"
$unoProject = Join-Path $repoRoot "src\UI\TaskFlow.Uno\TaskFlow.Uno.csproj"
$apkPath = Join-Path $repoRoot "src\UI\TaskFlow.Uno\bin\Debug\net10.0-android\com.taskflow.uno-Signed.apk"
$resultDir = Join-Path $repoRoot "tests\Test.Mobile\TestResults"

New-Item -ItemType Directory -Force -Path $resultDir | Out-Null

# Fail explicit mobile runs before MSTest starts when required local tools are
# absent. Non-enabled solution test runs stay inconclusive inside MSTest.
if (-not (Test-Path $AndroidSdk)) {
    throw "Android SDK not found at '$AndroidSdk'. Set ANDROID_HOME or ANDROID_SDK_ROOT."
}

$adb = Join-Path $AndroidSdk "platform-tools\adb.exe"
$emulator = Join-Path $AndroidSdk "emulator\emulator.exe"

if (-not (Test-Path $adb)) {
    throw "adb not found at '$adb'. Install Android SDK Platform Tools."
}

if (-not (Test-Path $emulator)) {
    throw "emulator.exe not found at '$emulator'. Install Android Emulator."
}

if (-not $SkipBuild) {
    # Uno defaults to fast browser-wasm restore. Android tests need all mobile
    # targets restored so platform-specific Skia assets land in the graph.
    & dotnet restore $unoProject -p:BuildAllUnoTargets=true
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    & dotnet build $unoProject -p:TargetFrameworkOverride=net10.0-android -p:UseMocks=true --no-restore -m:1
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# Always rebuild the small test project so script edits and test changes are not
# hidden by -SkipBuild. -SkipBuild only skips the expensive APK rebuild.
& dotnet restore $testProject
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& dotnet build $testProject --no-restore -m:1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (-not (Test-Path $apkPath)) {
    throw "Android APK not found at '$apkPath'. Run without -SkipBuild or build the Uno Android target first."
}

$readyDevice = & $adb devices | Select-String "`tdevice$"
if (-not $readyDevice) {
    $availableAvds = & $emulator -list-avds
    if ($availableAvds -notcontains $AvdName) {
        throw "AVD '$AvdName' not found. Available AVDs: $($availableAvds -join ', ')"
    }

    # Snapshot-free boot avoids stale app/test state between local runs.
    $emulatorArgs = @("-avd", $AvdName, "-no-snapshot-load", "-no-snapshot-save", "-no-audio", "-gpu", "swiftshader_indirect", "-no-boot-anim")
    if (-not $VisibleEmulator) {
        $emulatorArgs += "-no-window"
    }

    if ($VisibleEmulator) {
        Start-Process -FilePath $emulator -ArgumentList $emulatorArgs | Out-Null
    }
    else {
        Start-Process -FilePath $emulator -ArgumentList $emulatorArgs -WindowStyle Hidden | Out-Null
    }
}

Wait-Until `
    -Timeout ([TimeSpan]::FromSeconds($BootTimeoutSeconds)) `
    -FailureMessage "Android emulator did not boot within $BootTimeoutSeconds seconds." `
    -Probe {
        $devices = & $adb devices | Select-String "`tdevice$"
        if (-not $devices) { return $false }

        $boot = (& $adb shell getprop sys.boot_completed 2>$null | Select-Object -First 1)
        return $boot -eq "1"
    }

& $adb shell input keyevent 82 2>$null

if (-not (Test-AppiumReady $AppiumServerUrl)) {
    $appium = Get-Command appium -ErrorAction SilentlyContinue
    if (-not $appium) {
        throw "Appium CLI not found. Install Appium and the uiautomator2 driver."
    }

    $outLog = Join-Path $resultDir "appium.out.log"
    $errLog = Join-Path $resultDir "appium.err.log"
    # The mobile page objects use Appium's adb shell bridge to collapse Android
    # system UI and route text when Uno/Skia editors reject WebDriver SendKeys.
    Start-Process `
        -FilePath "powershell.exe" `
        -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $appium.Source, "--address", "127.0.0.1", "--port", "4723", "--allow-insecure=uiautomator2:adb_shell") `
        -RedirectStandardOutput $outLog `
        -RedirectStandardError $errLog `
        -WindowStyle Hidden | Out-Null
}

Wait-Until `
    -Timeout ([TimeSpan]::FromSeconds($AppiumTimeoutSeconds)) `
    -FailureMessage "Appium did not become ready within $AppiumTimeoutSeconds seconds. Check $resultDir\appium.err.log." `
    -Probe { Test-AppiumReady $AppiumServerUrl }

$env:TASKFLOW_MOBILE_TESTS_ENABLED = "true"
$env:TASKFLOW_MOBILE_PLATFORM = "Android"
$env:TASKFLOW_APPIUM_SERVER_URL = $AppiumServerUrl
$env:TASKFLOW_ANDROID_APP_PATH = $apkPath
$env:TASKFLOW_MOBILE_STARTUP_TIMEOUT_SECONDS = "120"

Push-Location $repoRoot
try {
    # Set enablement only after dependencies are ready. At this point missing
    # mobile infrastructure is a real failure, not an inconclusive test.
    & dotnet test $testProject --no-build -m:1 --filter $Filter --logger "console;verbosity=normal" --logger "trx;LogFileName=mobile.trx" --results-directory $resultDir
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
