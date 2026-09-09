namespace Test.Unit.Infrastructure;

/// <summary>
/// Locks the checked deployment ordering and immutable rollback contract for both lanes: the Azure lane
/// (deploy.yml), the Portable lane (deploy-vps.yml), and the reusable image build both of them call.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class DeploymentWorkflowContractTests
{
    [TestMethod]
    public void DeployWorkflow_PreservesSafeReleaseContract()
    {
        var workflow = ReadWorkflow("deploy.yml");

        StringAssert.Contains(workflow, "workflow_call:");
        StringAssert.Contains(workflow, "options: [deploy, rollback]");
        StringAssert.Contains(workflow, "commit_sha:");
        StringAssert.Contains(workflow, "cancel-in-progress: false");
        StringAssert.Contains(workflow, "artifact-id");
        StringAssert.Contains(workflow, "artifact-digest");
        StringAssert.Contains(workflow, "sha256sum --check --status");
        StringAssert.Contains(workflow, "previousManifestArtifactId");
        StringAssert.Contains(workflow, "Invoke-DeploymentSmoke.ps1");
        Assert.IsFalse(workflow.Contains("--build-arg", StringComparison.Ordinal));
        Assert.IsFalse(workflow.Contains("dotnet nuget update", StringComparison.Ordinal));
        Assert.IsFalse(workflow.Contains("--query '[0].name'", StringComparison.Ordinal));
        StringAssert.Contains(workflow, "Test-ReleaseManifest.ps1 -Path current/release-manifest.json");
        StringAssert.Contains(workflow, "Expected exactly one Function App matching");
        StringAssert.Contains(workflow, "expected_swa=\"${RESOURCE_PREFIX}-${ENVIRONMENT_NAME}-uno\"");

        // The image build lives in the reusable workflow now, and both lanes must consume the same one.
        StringAssert.Contains(workflow, "uses: ./.github/workflows/build-images.yml");
        Assert.IsFalse(workflow.Contains("docker buildx build", StringComparison.Ordinal));

        var ciWorkflow = ReadWorkflow("ci.yml");
        StringAssert.Contains(ciWorkflow, "NuGetPackageSourceCredentials_efreeman518-github");
        Assert.IsFalse(ciWorkflow.Contains("dotnet nuget update", StringComparison.Ordinal));

        var migration = workflow.IndexOf("  run-migrations:", StringComparison.Ordinal);
        var activation = workflow.IndexOf("  activate-release:", StringComparison.Ordinal);
        Assert.IsGreaterThan(0, migration);
        Assert.IsGreaterThan(migration, activation, "New runtime activation must remain after the migration job.");

        var rollback = workflow[workflow.IndexOf("  rollback:", StringComparison.Ordinal)..];
        Assert.IsFalse(rollback.Contains("docker build", StringComparison.Ordinal));
        Assert.IsFalse(rollback.Contains("dotnet publish", StringComparison.Ordinal));

        var smoke = File.ReadAllText(RepoRoot.Combine("infra", "scripts", "Invoke-DeploymentSmoke.ps1"));
        StringAssert.Contains(smoke, "/health/full");
        StringAssert.Contains(smoke, "finally");
        StringAssert.Contains(smoke, "-Method Delete");

        var manifestValidator = File.ReadAllText(
            RepoRoot.Combine("infra", "scripts", "Test-ReleaseManifest.ps1"));
        StringAssert.Contains(manifestValidator, "'^[0-9a-f]{64}$'");
        // The Portable lane ships no Functions/Uno artifacts, so its manifest is images-only by switch
        // rather than by a weaker schema.
        StringAssert.Contains(manifestValidator, "[switch] $ImagesOnly");
        StringAssert.Contains(manifestValidator, "if (-not $ImagesOnly) {");
    }

    /// <summary>
    /// One image build for both lanes. The credential must stay a BuildKit secret and the reference handed
    /// downstream must stay a registry digest - a tag would let the two lanes ship different bits.
    /// </summary>
    [TestMethod]
    public void BuildImagesWorkflow_IsReusableAndPinsEveryImageByDigest()
    {
        var workflow = ReadWorkflow("build-images.yml");

        StringAssert.Contains(workflow, "workflow_call:");
        StringAssert.Contains(workflow, "runs-on: ubuntu-latest");
        StringAssert.Contains(workflow, "--secret id=nuget_credentials");
        StringAssert.Contains(workflow, "@${digest}");
        StringAssert.Contains(workflow, "^sha256:[0-9a-f]{64}$");
        Assert.IsFalse(workflow.Contains("--build-arg", StringComparison.Ordinal));
        Assert.IsFalse(workflow.Contains("store-password-in-clear-text", StringComparison.Ordinal));

        foreach (var image in new[]
        {
            "build_image gateway taskflow-gateway ./src/Host/TaskFlow.Gateway/Dockerfile",
            "build_image api taskflow-api ./src/Host/TaskFlow.Api/Dockerfile",
            "build_image scheduler taskflow-scheduler ./src/Host/TaskFlow.Scheduler/Dockerfile",
            "build_image migrator taskflow-db-migrator ./src/Host/TaskFlow.DatabaseMigrator/Dockerfile",
            "build_image blazor taskflow-blazor ./src/UI/TaskFlow.Blazor/Dockerfile"
        })
        {
            StringAssert.Contains(workflow, image);
        }

        foreach (var caller in new[] { "deploy.yml", "deploy-vps.yml" })
        {
            StringAssert.Contains(ReadWorkflow(caller), "uses: ./.github/workflows/build-images.yml", caller);
        }
    }

    /// <summary>
    /// D-036: the Portable lane release must be manual, serialized, digest-pinned, verified through the
    /// public edge, and reversible from a recorded manifest without rebuilding anything.
    /// </summary>
    [TestMethod]
    public void DeployVpsWorkflow_IsManualSerializedAndReversible()
    {
        var workflow = ReadWorkflow("deploy-vps.yml");

        StringAssert.Contains(workflow, "workflow_dispatch:");
        StringAssert.Contains(workflow, "options: [deploy, rollback]");
        StringAssert.Contains(workflow, "group: taskflow-deploy-vps");
        StringAssert.Contains(workflow, "cancel-in-progress: false");
        Assert.IsFalse(workflow.Contains("on:\n  push:", StringComparison.Ordinal));

        // Same green-commit gate as the Azure lane.
        StringAssert.Contains(workflow, "git merge-base --is-ancestor");
        StringAssert.Contains(workflow, "has no successful build-and-test check");

        // Digest pins, both directions.
        StringAssert.Contains(workflow, "Refusing to deploy an image that is not digest-pinned");
        StringAssert.Contains(workflow, "Recorded image is not digest-pinned");
        StringAssert.Contains(workflow, "TASKFLOW_API_IMAGE=$API");
        StringAssert.Contains(workflow, "docker compose -f docker-compose.yml");
        StringAssert.Contains(workflow, "up -d --wait");
        StringAssert.Contains(workflow, "/healthz/ready");
        StringAssert.Contains(workflow, "previousManifestArtifactId");
        StringAssert.Contains(workflow, "taskflow-release-manifest-vps");

        // Plain ssh with a pinned host key: no third-party action holds a key that can run docker on the box.
        StringAssert.Contains(workflow, "StrictHostKeyChecking yes");
        StringAssert.Contains(workflow, "VPS_KNOWN_HOSTS");
        Assert.IsFalse(workflow.Contains("StrictHostKeyChecking no", StringComparison.Ordinal));
        Assert.IsFalse(workflow.Contains("StrictHostKeyChecking=no", StringComparison.Ordinal));
        Assert.IsFalse(workflow.Contains("appleboy/", StringComparison.Ordinal));

        // Secret names only - never a literal host, user or key.
        foreach (var secret in new[] { "VPS_HOST", "VPS_USER", "VPS_SSH_KEY", "VPS_KNOWN_HOSTS", "CADDY_DOMAIN" })
        {
            StringAssert.Contains(workflow, $"secrets.{secret}", secret);
        }

        var rollback = workflow[workflow.IndexOf("  rollback:", StringComparison.Ordinal)..];
        Assert.IsFalse(rollback.Contains("docker buildx build", StringComparison.Ordinal));
        Assert.IsFalse(rollback.Contains("dotnet publish", StringComparison.Ordinal));
    }

    /// <summary>
    /// The cheap compose schema check runs on every CI run; the expensive stack smoke stays dispatch-gated,
    /// and must always dump logs so a failure is diagnosable without a rerun.
    /// </summary>
    [TestMethod]
    public void CiWorkflow_ValidatesComposeAlwaysAndSmokesItOnlyOnDispatch()
    {
        var workflow = ReadWorkflow("ci.yml");

        // PR runs are the merge gate and both deploy workflows are workflow_dispatch only, so a main-push
        // run of ci.yml would only re-test the identical tree the PR run already verified.
        Assert.IsFalse(workflow.Contains("\n  push:", StringComparison.Ordinal), "ci.yml must not run on push");
        StringAssert.Contains(workflow, "  pull_request:");
        StringAssert.Contains(workflow, "  schedule:");
        StringAssert.Contains(workflow, "  workflow_dispatch:");

        // Uno.Sdk conditions implicit package references (DevServer, HotDesign, MCP) on Optimize: a Debug restore
        // followed by a Release --no-restore build fails with UNOB0019, so every restore names the Release configuration.
        StringAssert.Contains(workflow, "dotnet restore TaskFlow.slnx -p:Configuration=Release");
        Assert.AreEqual(
            workflow.Split("dotnet restore TaskFlow.slnx").Length,
            workflow.Split("dotnet restore TaskFlow.slnx -p:Configuration=Release").Length,
            "every solution restore in ci.yml must name the Release configuration");

        StringAssert.Contains(workflow, "docker compose -f docker-compose.yml config -q");
        StringAssert.Contains(
            workflow,
            "docker compose -f docker-compose.yml -f docker-compose.override.local.yml config -q");
        StringAssert.Contains(workflow, "includeComposeSmoke:");
        StringAssert.Contains(workflow, "  compose-smoke:");
        StringAssert.Contains(workflow, "inputs.includeComposeSmoke == true");
        StringAssert.Contains(workflow, "http://localhost/healthz/ready");
        StringAssert.Contains(workflow, "/api/v1/task-items");

        var smokeJob = workflow[workflow.IndexOf("  compose-smoke:", StringComparison.Ordinal)..];
        StringAssert.Contains(smokeJob, "runs-on: ubuntu-latest");
        StringAssert.Contains(smokeJob, "logs --no-color --tail 400");
        var logDump = smokeJob.IndexOf("Dump stack logs", StringComparison.Ordinal);
        Assert.IsGreaterThan(0, logDump);
        StringAssert.Contains(smokeJob[logDump..], "if: always()");
    }

    /// <summary>
    /// D-036 compose invariants: only the edge is exposed, Grafana is loopback-only, app containers restart
    /// on their own, and the migrator is the single schema owner every app waits on. The app services must
    /// carry no healthcheck - the runtime images are chiseled, so a probe would have no binary to exec.
    /// </summary>
    [TestMethod]
    public void ComposeLane_ExposesOnlyTheEdgeAndWaitsOnTheMigrator()
    {
        var compose = File.ReadAllText(RepoRoot.Combine("deploy", "compose", "docker-compose.yml"));

        StringAssert.Contains(compose, "restart: unless-stopped");
        StringAssert.Contains(compose, "condition: service_completed_successfully");
        StringAssert.Contains(compose, "condition: service_healthy");
        StringAssert.Contains(compose, "limits:");
        StringAssert.Contains(compose, "\"127.0.0.1:3000:3000\"");
        StringAssert.Contains(compose, "profiles: [\"pooler\"]");

        // Only caddy publishes to the outside world; the one other mapping is Grafana on loopback.
        var published = System.Text.RegularExpressions.Regex
            .Matches(compose, "^\\s+- \"(?<map>[0-9.:]+)\"\\s*$", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(match => match.Groups["map"].Value)
            .ToArray();
        CollectionAssert.AreEquivalent(new[] { "80:80", "443:443", "127.0.0.1:3000:3000" }, published);
        CollectionAssert.AreEquivalent(
            new[] { "80:80", "443:443" }, ServiceBlock(compose, "caddy").Ports().ToArray());

        // Chiseled runtime images have no shell and no curl, so the only healthchecks belong to the
        // infrastructure containers and to caddy's own binary.
        foreach (var appService in new[] { "api", "gateway", "scheduler", "blazor", "migrator" })
        {
            Assert.IsFalse(
                ServiceBlock(compose, appService).Text.Contains("healthcheck:", StringComparison.Ordinal),
                appService);
        }

        var local = File.ReadAllText(
            RepoRoot.Combine("deploy", "compose", "docker-compose.override.local.yml"));
        StringAssert.Contains(local, "pgvector/pgvector:pg17");
        StringAssert.Contains(local, "rabbitmq:4-management");
        StringAssert.Contains(local, "minio/minio");
        StringAssert.Contains(local, "pg_isready");
        StringAssert.Contains(local, "rabbitmq-diagnostics");
        StringAssert.Contains(local, "\"mc\", \"ready\", \"local\"");
        StringAssert.Contains(local, "- nuget_credentials");
        StringAssert.Contains(local, "environment: NuGetPackageSourceCredentials_efreeman518-github");

        // The environment contract is names only: a committed value here would be a leaked secret.
        var envExample = File.ReadAllText(RepoRoot.Combine("deploy", "compose", ".env.example"));
        foreach (var name in new[]
        {
            "TASKFLOW_LANE", "TASKFLOW_STORAGE_PROVIDER", "Database__PostgreSql__PoolerMode",
            "ConnectionStrings__TaskFlowDbContextTrxn", "ConnectionStrings__Redis1",
            "ConnectionStrings__RabbitMq1", "Storage__S3__PublicServiceUrl", "AppConfig__Endpoint",
            "AZURE_CLIENT_SECRET", "AZURE_CLIENT_CERTIFICATE_PATH", "DataProtectionEncryptionKeyUrl",
            "Database__Encryption__LocalKeyBase64", "Grpc__TaskFlowRead__Address",
            "OTEL_EXPORTER_OTLP_ENDPOINT", "CADDY_DOMAIN", "ACME_EMAIL", "TASKFLOW_API_IMAGE"
        })
        {
            StringAssert.Contains(envExample, $"\n{name}=", name);
        }

        foreach (var line in envExample.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            Assert.EndsWith("=", trimmed, $"'{trimmed}' must declare a name without a value.");
        }

        var gitignore = File.ReadAllText(RepoRoot.Combine(".gitignore"));
        StringAssert.Contains(gitignore, "deploy/compose/.env");
        StringAssert.Contains(gitignore, "deploy/compose/images.env");
    }

    /// <summary>
    /// Every deployed image is built the same way: private feed as a BuildKit secret, chiseled runtime,
    /// non-root. Gateway is the only host with neither a culture-rendering nor a Microsoft.Data.SqlClient
    /// reason to need ICU (D-047), so it alone stays on the plain chiseled base; every other host takes
    /// the -extra variant.
    /// </summary>
    [TestMethod]
    public void Dockerfiles_UseTheSecretMountAndTheRightChiseledBase()
    {
        foreach (var (dockerfile, runtimeBase) in new[]
        {
            ("src/Host/TaskFlow.Gateway/Dockerfile", "10.0-noble-chiseled"),
            ("src/Host/TaskFlow.Api/Dockerfile", "10.0-noble-chiseled-extra"),
            ("src/Host/TaskFlow.Scheduler/Dockerfile", "10.0-noble-chiseled-extra"),
            ("src/Host/TaskFlow.DatabaseMigrator/Dockerfile", "10.0-noble-chiseled-extra"),
            ("src/Host/TaskFlow.Functions/Dockerfile", "10.0-noble-chiseled-extra"),
            ("src/UI/TaskFlow.Blazor/Dockerfile", "10.0-noble-chiseled-extra")
        })
        {
            var content = File.ReadAllText(RepoRoot.Combine(dockerfile.Split('/')));
            StringAssert.Contains(content, "--mount=type=secret,id=nuget_credentials,required=true", dockerfile);
            StringAssert.Contains(
                content, $"FROM mcr.microsoft.com/dotnet/aspnet:{runtimeBase} AS runtime", dockerfile);
            StringAssert.Contains(content, "USER $APP_UID", dockerfile);
            Assert.IsFalse(content.Contains("ARG NUGET_TOKEN", StringComparison.Ordinal), dockerfile);
            Assert.IsFalse(content.Contains("store-password-in-clear-text", StringComparison.Ordinal), dockerfile);
            // A literal backslash-n instead of a line continuation silently hands dotnet publish a stray
            // positional argument, which is exactly how the Gateway image build was broken.
            Assert.IsFalse(content.Contains("\\n", StringComparison.Ordinal), dockerfile);
        }
    }

    /// <summary>
    /// One compose service's block. Matched on a two-space indented key so the same name nested under
    /// <c>depends_on:</c> cannot be mistaken for the service definition, and ended at the next top-level
    /// mapping key rather than at any indented line.
    /// </summary>
    private readonly record struct ComposeService(string Text)
    {
        public IEnumerable<string> Ports() => System.Text.RegularExpressions.Regex
            .Matches(Text, "^\\s+- \"(?<map>[0-9.:]+)\"\\s*$", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(match => match.Groups["map"].Value);
    }

    private static ComposeService ServiceBlock(string compose, string name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            compose,
            $"^  {name}:\\r?$\\n(?<body>(?:^(?:    .*|\\s*)\\r?$\\n?)*)",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        Assert.IsTrue(match.Success, $"Service '{name}' not found as a top-level compose service.");
        return new ComposeService(match.Groups["body"].Value);
    }

    private static string ReadWorkflow(string name) =>
        File.ReadAllText(RepoRoot.Combine(".github", "workflows", name));
}
