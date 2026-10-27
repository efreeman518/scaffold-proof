# TaskFlow Mobile UI Tests

MSTest + Appium smoke coverage for the Uno Android/iOS heads.

Default test runs are opt-in. Without `TASKFLOW_MOBILE_TESTS_ENABLED=true`, mobile tests report Inconclusive and do not touch Appium.

## Dependency Contract

Test methods validate mobile UI behavior; they do not launch Appium or emulators. Dependency startup belongs to `run-mobile-tests.ps1`, CI, or a developer shell.

- Default full-suite run: inconclusive when mobile lane is not enabled.
- Explicit mobile lane: missing APK, emulator, Appium, or UiAutomator2 fails fast.
- Runner path: build APK, start or verify Android emulator, start or verify Appium, run `dotnet test`.
- Native Appium scope: stable launch, first-viewport accessibility, and text-entry smoke. Deep CRUD, search persistence, child entity persistence, and long-scroll behavior stay in API, unit, integration, and Playwright lanes.

## Prerequisites

Install these before running Android mobile UI tests:

- Node.js for Appium CLI.
- Appium plus UiAutomator2 driver.
- Android Studio or Android SDK command-line tools, including Android SDK Platform Tools, Android Emulator, and a system image.
- An Android Virtual Device. Default runner AVD name is `Android_Emulator_35`; override with `-AvdName` or `TASKFLOW_MOBILE_AVD_NAME`.

Quick install/check commands:

```powershell
node --version
npm --version
npm install -g appium
appium driver install uiautomator2
appium driver list --installed
```

If Android SDK tools are not on `PATH`, use the installed SDK path directly:

```powershell
$env:ANDROID_HOME = "C:\Program Files (x86)\Android\android-sdk"
& "$env:ANDROID_HOME\emulator\emulator.exe" -list-avds
& "$env:ANDROID_HOME\platform-tools\adb.exe" devices -l
```

## Android Local Run

Use the runner:

```powershell
powershell -NoProfile -File tests/Test.Mobile/run-mobile-tests.ps1
```

Common options:

```powershell
powershell -NoProfile -File tests/Test.Mobile/run-mobile-tests.ps1 -SkipBuild
powershell -NoProfile -File tests/Test.Mobile/run-mobile-tests.ps1 -AvdName Android_Emulator_35
powershell -NoProfile -File tests/Test.Mobile/run-mobile-tests.ps1 -VisibleEmulator
powershell -NoProfile -File tests/Test.Mobile/run-mobile-tests.ps1 -VisibleEmulator -AvdName Android_Emulator_35
powershell -NoProfile -File tests/Test.Mobile/run-mobile-tests.ps1 -SkipBuild -Filter "FullyQualifiedName~TaskFlowMobile_AppLaunches_AndRendersNativeSurface"
powershell -NoProfile -File tests/Test.Mobile/run-mobile-tests.ps1 -AndroidSdk "C:\Program Files (x86)\Android\android-sdk" -AppiumServerUrl "http://127.0.0.1:4723/"
```

Parameter notes:

- `-SkipBuild`: skip expensive Android APK rebuild, but still restore/build `Test.Mobile`.
- `-VisibleEmulator`: start emulator with a visible window; default is headless.
- `-AvdName`: override the default `Android_Emulator_35`; `TASKFLOW_MOBILE_AVD_NAME` also works.
- `-Filter`: pass an MSTest filter for focused smoke runs.
- `-AndroidSdk`: override `ANDROID_HOME` / `ANDROID_SDK_ROOT` discovery.
- `-AppiumServerUrl`: use an already-running non-default Appium endpoint.

The runner performs the required Android restore/build:

```powershell
dotnet restore src/UI/TaskFlow.Uno/TaskFlow.Uno.csproj -p:BuildAllUnoTargets=true
dotnet build src/UI/TaskFlow.Uno/TaskFlow.Uno.csproj -p:TargetFrameworkOverride=net11.0-android -p:UseMocks=true --no-restore -m:1
```

`BuildAllUnoTargets=true` is required because the Uno project defaults to fast Wasm-only restore for local web work. Android/Appium runs need platform-specific Skia runtime packages in the NuGet asset graph.

## Direct Test Run

Only use direct `dotnet test` after the emulator and Appium are already running:

```powershell
$env:TASKFLOW_MOBILE_TESTS_ENABLED = "true"
$env:TASKFLOW_MOBILE_PLATFORM = "Android"
$env:TASKFLOW_ANDROID_APP_PATH = "src/UI/TaskFlow.Uno/bin/Debug/net11.0-android/com.taskflow.uno-Signed.apk"
dotnet test tests/Test.Mobile/Test.Mobile.csproj --no-build --filter TestCategory=MobileUI
```

Appium must be started with UiAutomator2 shell support:

```powershell
appium --address 127.0.0.1 --port 4723 --allow-insecure=uiautomator2:adb_shell
```

## Troubleshooting

- `Mobile app package not found`: rebuild Android package or point `TASKFLOW_ANDROID_APP_PATH` at the APK.
- Appium unavailable: check `http://127.0.0.1:4723/status` and `appium driver list --installed`.
- No device from `adb devices -l`: start emulator and wait for `sys.boot_completed=1`.
- AVD exists but no emulator window appears: runner defaults headless; pass `-VisibleEmulator`.
- Android restore/build fails after normal Wasm-only restore: rerun the runner without `-SkipBuild`.

## iOS Gate

iOS simulator/device execution requires macOS or a Mac host with Xcode and Appium XCUITest support. Windows keeps the MSTest project compiling, but cannot run iOS simulator/device tests locally.
