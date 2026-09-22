namespace Test.Mobile;

/// <summary>Supports test execution for Test.mobile scenarios.</summary>
internal sealed record MobileTestSettings
{
    public required bool Enabled { get; init; }
    public required string RepoRoot { get; init; }
    public required bool HasConfiguredAppPath { get; init; }
    public required MobileTestPlatform Platform { get; init; }
    public required Uri AppiumServerUri { get; init; }
    public required string AppPath { get; init; }
    public required string DeviceName { get; init; }
    public required string ScreenshotDirectory { get; init; }
    public string? PlatformVersion { get; init; }
    public string AndroidAppPackage { get; init; } = "com.taskflow.uno";
    public string AndroidAppWaitActivity { get; init; } = "*";
    public string IosBundleId { get; init; } = "com.taskflow.uno";
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan AdbExecTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public TimeSpan UiAutomator2ServerLaunchTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public TimeSpan AndroidInstallTimeout { get; init; } = TimeSpan.FromSeconds(180);

    /// <summary>Verifies from behavior and protects the expected test contract.</summary>
    public static MobileTestSettings From(TestContext context)
    {
        var platform = ParsePlatform(GetValue(context, "TASKFLOW_MOBILE_PLATFORM") ?? "Android");
        var repoRoot = FindRepoRoot();
        var configuredAppPath = GetValue(context, PlatformAppPathKey(platform));
        var appPath = ResolveAppPath(repoRoot, configuredAppPath, platform);
        var screenshotDirectory = GetValue(context, "TASKFLOW_MOBILE_SCREENSHOT_DIR")
            ?? Path.Combine(repoRoot, "tests", "TestResults", "Mobile", "screenshots");

        return new MobileTestSettings
        {
            Enabled = IsTrue(GetValue(context, "TASKFLOW_MOBILE_TESTS_ENABLED")),
            RepoRoot = repoRoot,
            HasConfiguredAppPath = !string.IsNullOrWhiteSpace(configuredAppPath),
            Platform = platform,
            AppiumServerUri = new Uri(GetValue(context, "TASKFLOW_APPIUM_SERVER_URL") ?? "http://127.0.0.1:4723/"),
            AppPath = appPath,
            DeviceName = GetValue(context, "TASKFLOW_MOBILE_DEVICE_NAME") ?? DefaultDeviceName(platform),
            PlatformVersion = GetValue(context, "TASKFLOW_MOBILE_PLATFORM_VERSION"),
            ScreenshotDirectory = screenshotDirectory,
            AndroidAppPackage = GetValue(context, "TASKFLOW_ANDROID_APP_PACKAGE") ?? "com.taskflow.uno",
            AndroidAppWaitActivity = GetValue(context, "TASKFLOW_ANDROID_APP_WAIT_ACTIVITY") ?? "*",
            IosBundleId = GetValue(context, "TASKFLOW_IOS_BUNDLE_ID") ?? "com.taskflow.uno",
            StartupTimeout = TimeSpan.FromSeconds(ParsePositiveInt(GetValue(context, "TASKFLOW_MOBILE_STARTUP_TIMEOUT_SECONDS"), 60)),
            AdbExecTimeout = TimeSpan.FromSeconds(ParsePositiveInt(GetValue(context, "TASKFLOW_MOBILE_ADB_EXEC_TIMEOUT_SECONDS"), 120)),
            UiAutomator2ServerLaunchTimeout = TimeSpan.FromSeconds(ParsePositiveInt(GetValue(context, "TASKFLOW_MOBILE_UIAUTOMATOR2_TIMEOUT_SECONDS"), 120)),
            AndroidInstallTimeout = TimeSpan.FromSeconds(ParsePositiveInt(GetValue(context, "TASKFLOW_MOBILE_ANDROID_INSTALL_TIMEOUT_SECONDS"), 180))
        };
    }

    public static string DisabledMessage =>
        "Mobile UI tests are opt-in. Set TASKFLOW_MOBILE_TESTS_ENABLED=true, start Appium, build the app package, " +
        "then run dotnet test tests/Test.Mobile/Test.Mobile.csproj --filter TestCategory=MobileUI. " +
        "For Android, restore first with -p:BuildAllUnoTargets=true before the TargetFrameworkOverride build.";

    /// <summary>Verifies get value behavior and protects the expected test contract.</summary>
    private static string? GetValue(TestContext context, string key)
    {
        var env = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        if (context.Properties.TryGetValue(key, out var value) && value is not null)
        {
            var text = value.ToString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    /// <summary>Verifies parse platform behavior and protects the expected test contract.</summary>
    private static MobileTestPlatform ParsePlatform(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "android" => MobileTestPlatform.Android,
            "ios" => MobileTestPlatform.Ios,
            _ => throw new InvalidOperationException($"Unsupported TASKFLOW_MOBILE_PLATFORM '{value}'. Use Android or iOS.")
        };

    /// <summary>Verifies is true behavior and protects the expected test contract.</summary>
    private static bool IsTrue(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);

    /// <summary>Verifies parse positive int behavior and protects the expected test contract.</summary>
    private static int ParsePositiveInt(string? value, int fallback) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;

    /// <summary>Verifies platform app path key behavior and protects the expected test contract.</summary>
    private static string PlatformAppPathKey(MobileTestPlatform platform) =>
        platform == MobileTestPlatform.Android ? "TASKFLOW_ANDROID_APP_PATH" : "TASKFLOW_IOS_APP_PATH";

    /// <summary>Resolves configured app paths against the repo root so Test Explorer and CLI runs agree.</summary>
    private static string ResolveAppPath(string repoRoot, string? configuredPath, MobileTestPlatform platform)
    {
        var appPath = string.IsNullOrWhiteSpace(configuredPath)
            ? GetDefaultAppPath(repoRoot, platform)
            : configuredPath.Trim();

        if (Path.IsPathFullyQualified(appPath))
        {
            return appPath;
        }

        return Path.GetFullPath(appPath, repoRoot);
    }

    /// <summary>Verifies default device name behavior and protects the expected test contract.</summary>
    private static string DefaultDeviceName(MobileTestPlatform platform) =>
        platform == MobileTestPlatform.Android ? "Android Emulator" : "iPhone Simulator";

    /// <summary>Verifies get default app path behavior and protects the expected test contract.</summary>
    private static string GetDefaultAppPath(string repoRoot, MobileTestPlatform platform) =>
        platform switch
        {
            MobileTestPlatform.Android => Path.Combine(
                repoRoot,
                "src",
                "UI",
                "TaskFlow.Uno",
                "bin",
                "Debug",
                "net10.0-android",
                "com.taskflow.uno-Signed.apk"),
            MobileTestPlatform.Ios => Path.Combine(
                repoRoot,
                "src",
                "UI",
                "TaskFlow.Uno",
                "bin",
                "Debug",
                "net10.0-ios",
                "iossimulator-x64",
                "TaskFlow.Uno.app"),
            _ => throw new InvalidOperationException($"Unsupported platform '{platform}'.")
        };

    /// <summary>Verifies find source root behavior and protects the expected test contract.</summary>
    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TaskFlow.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate TaskFlow.slnx from the test output directory.");
    }
}
