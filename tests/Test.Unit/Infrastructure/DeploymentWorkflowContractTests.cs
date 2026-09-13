namespace Test.Unit.Infrastructure;

/// <summary>
/// Locks the checked deployment ordering and immutable rollback contract for both lanes: the Azure lane
/// (deploy.yml), the NonAzure lane (deploy-vps.yml), and the reusable image build both of them call.
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
        StringAssert.Contains(workflow, "expected_react_swa=\"${RESOURCE_PREFIX}-${ENVIRONMENT_NAME}-react\"");
        StringAssert.Contains(workflow, "expected_uno_swa=\"${RESOURCE_PREFIX}-${ENVIRONMENT_NAME}-uno\"");
        StringAssert.Contains(workflow, "taskflow-react-${{ needs.validate-entry.outputs.sha }}");
        StringAssert.Contains(workflow, "app-config.json");
        StringAssert.Contains(workflow, "-ReactUrl");
        StringAssert.Contains(workflow, "-UnoUrl");

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
        // v1 remains readable for historical release records; v2 is required before a React/Uno release can
        // name a previous manifest as a rollback target.
        StringAssert.Contains(manifestValidator, "[switch] $ImagesOnly");
        StringAssert.Contains(manifestValidator, "[int] $ExpectedSchemaVersion");
        StringAssert.Contains(manifestValidator, "schemaVersion must be 1 or 2");
        StringAssert.Contains(workflow, "Skipping incompatible v1 release manifest");
        StringAssert.Contains(workflow, "-ExpectedSchemaVersion 2");
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
            "build_image blazor taskflow-blazor ./src/UI/TaskFlow.Blazor/Dockerfile",
            "build_image react taskflow-react ./src/UI/TaskFlow.React/Dockerfile",
            "build_image uno taskflow-uno ./src/UI/TaskFlow.Uno/Dockerfile"
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
    /// D-060/D-036: the NonAzure lane release must be manual, serialized, digest-pinned, verified through the
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
        StringAssert.Contains(workflow, "'{schemaVersion:2,operation:");
        StringAssert.Contains(workflow, "-ExpectedSchemaVersion 2");
        StringAssert.Contains(workflow, "name: Deploy TaskFlow (NonAzure lane, VPS)");
        Assert.IsFalse(workflow.Contains("Portable lane", StringComparison.Ordinal));

        // .env.base is the only operator-managed source. Generated .env is rebuilt from it and immutable
        // image pins; a clean VPS receives the current template but deployment never promotes generated
        // output back into the operator source.
        StringAssert.Contains(workflow, "deploy/compose/.env.example \"taskflow-vps:~/${REMOTE_DIR}/.env.base.example\"");
        StringAssert.Contains(workflow, "[[ -f .env.base ]]");
        StringAssert.Contains(workflow, "Refusing deployment while .env.base contains a CHANGE_ME value.");
        StringAssert.Contains(workflow, "{ cat .env.base; printf '\\n'; cat images.env; } > .env");
        StringAssert.Contains(workflow, "Remove image variables from .env.base; images.env is workflow-managed.");
        StringAssert.Contains(workflow, "${COMPOSE} config -q");
        Assert.IsFalse(workflow.Contains("cp .env .env.base", StringComparison.Ordinal));

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

    [TestMethod]
    public void CiWorkflow_UnitGateRunsWholeProjectWithHardHangCeilings()
    {
        var workflow = ReadWorkflow("ci.yml");
        var unitStart = workflow.IndexOf("      - name: Unit Tests", StringComparison.Ordinal);
        var architectureStart = workflow.IndexOf("      - name: Architecture Tests", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, unitStart);
        Assert.IsGreaterThan(unitStart, architectureStart);

        var unitStep = workflow[unitStart..architectureStart];
        StringAssert.Contains(unitStep, "timeout-minutes: 1");
        StringAssert.Contains(unitStep, "timeout --signal=INT --kill-after=5s 50s");
        StringAssert.Contains(unitStep, "dotnet test tests/Test.Unit/Test.Unit.csproj");
        StringAssert.Contains(unitStep, "--blame-hang --blame-hang-timeout 15s");
        Assert.IsFalse(
            unitStep.Contains("TestCategory=Unit", StringComparison.Ordinal),
            "The complete Test.Unit project includes untagged provider and regression contracts.");
        StringAssert.Contains(workflow, "./TestResults/**/*.dmp");
    }

    [TestMethod]
    public void CiWorkflow_SeparatesDeterministicTopologyFromManualRunnableFullLanes()
    {
        var workflow = ReadWorkflow("ci.yml");
        var topologyStart = workflow.IndexOf("      - name: Aspire lane topology contracts", StringComparison.Ordinal);
        var meshStart = workflow.IndexOf("      - name: Aspire Core Lane Mesh Tests (manual)", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, topologyStart);
        Assert.IsGreaterThan(topologyStart, meshStart);

        var topologyStep = workflow[topologyStart..meshStart];
        StringAssert.Contains(topologyStep, "FullyQualifiedName~AppHostLaneTopologyTests");
        StringAssert.Contains(topologyStep, "FullyQualifiedName~AppHostMigratorTopologyTests");
        Assert.IsFalse(topologyStep.Contains("if:", StringComparison.Ordinal),
            "Deterministic graph contracts must run on pull requests.");

        var fullStart = workflow.IndexOf("  full-lane-acceptance:", StringComparison.Ordinal);
        var databaseStart = workflow.IndexOf("  database-lanes:", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, fullStart);
        Assert.IsGreaterThan(fullStart, databaseStart);
        var fullJob = workflow[fullStart..databaseStart];

        StringAssert.Contains(fullJob, "inputs.includeFullAcceptance == true");
        StringAssert.Contains(fullJob, "runs-on: [self-hosted, workstation]");
        StringAssert.Contains(fullJob, "TASKFLOW_ASPIRE_FULL_LANE: \"true\"");
        StringAssert.Contains(fullJob, "TASKFLOW_ASPIRE_SCHEDULER_AVAILABLE: \"true\"");
        StringAssert.Contains(fullJob, "TASKFLOW_REACT_TESTS_ENABLED: \"true\"");
        StringAssert.Contains(fullJob, "TASKFLOW_WASM_TESTS_ENABLED: \"true\"");
        StringAssert.Contains(fullJob, "TASKFLOW_PLAYWRIGHT_TESTS_ENABLED: \"true\"");
        StringAssert.Contains(fullJob, "dotnet test tests/Test.Aspire/Test.Aspire.csproj");
        StringAssert.Contains(fullJob, "FullyQualifiedName~AppSurfaceAspireTests");
        StringAssert.Contains(fullJob, "FullyQualifiedName~OutboxMeshTests");
        StringAssert.Contains(fullJob, "FullyQualifiedName~FunctionAuditPipelineTests");
        StringAssert.Contains(fullJob, "dotnet test tests/Test.PlaywrightUI/Test.PlaywrightUI.csproj");
        StringAssert.Contains(fullJob, "TestCategory=PlaywrightUI|TestCategory=WasmUI");
        StringAssert.Contains(fullJob, "Invoke-LaneAcceptance -Lane Azure -ReadModel Cosmos -RunFunctions $true");
        StringAssert.Contains(fullJob, "Invoke-LaneAcceptance -Lane NonAzure");
        StringAssert.Contains(fullJob, "-RunFunctions $false");
        Assert.IsFalse(fullJob.Contains("runs-on: ubuntu-latest", StringComparison.Ordinal));
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
        // run of ci.yml would only re-test the identical tree the PR run already verified. Anchored at line
        // start (not a fixed-indentation substring) so a reflowed "on:" block still gets checked correctly.
        const System.Text.RegularExpressions.RegexOptions Multiline = System.Text.RegularExpressions.RegexOptions.Multiline;
        Assert.IsFalse(
            System.Text.RegularExpressions.Regex.IsMatch(workflow, @"^\s*push:\s*$", Multiline),
            "ci.yml must not run on push");
        Assert.IsTrue(
            System.Text.RegularExpressions.Regex.IsMatch(workflow, @"^\s*pull_request:\s*$", Multiline));
        Assert.IsFalse(
            System.Text.RegularExpressions.Regex.IsMatch(workflow, @"^\s*schedule:\s*$", Multiline),
            "ci.yml must not spend Actions minutes on scheduled runs.");
        Assert.IsTrue(
            System.Text.RegularExpressions.Regex.IsMatch(workflow, @"^\s*workflow_dispatch:\s*$", Multiline));

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
        StringAssert.Contains(workflow, "Verify static UI roots and runtime gateway configuration");
        StringAssert.Contains(workflow, "check_ui react.localhost");
        StringAssert.Contains(workflow, "check_ui uno.localhost");
        StringAssert.Contains(workflow, ".gatewayBaseUrl == $gateway");

        var smokeJob = workflow[workflow.IndexOf("  compose-smoke:", StringComparison.Ordinal)..];
        StringAssert.Contains(smokeJob, "runs-on: ubuntu-latest");
        StringAssert.Contains(smokeJob, "ReadModel__Provider=${{ inputs.nonAzureReadModel }}");
        StringAssert.Contains(smokeJob, "REDIS_PASSWORD=$redis_password");
        StringAssert.Contains(smokeJob, "ConnectionStrings__Redis1=redis:6379,password=$redis_password,abortConnect=false");
        StringAssert.Contains(smokeJob, "MONGO_INITDB_ROOT_USERNAME=taskflow-ci");
        StringAssert.Contains(smokeJob, "MONGO_INITDB_ROOT_PASSWORD=$mongo_password");
        StringAssert.Contains(smokeJob, "ConnectionStrings__MongoDb1=mongodb://taskflow-ci:$mongo_password@mongo:27017/taskflow?authSource=admin");
        StringAssert.Contains(smokeJob, "openssl rand -hex 24");
        StringAssert.Contains(smokeJob, "::add-mask::$redis_password");
        StringAssert.Contains(smokeJob, "::add-mask::$mongo_password");
        StringAssert.Contains(smokeJob, "logs --no-color --tail 400");
        var logDump = smokeJob.IndexOf("Dump stack logs", StringComparison.Ordinal);
        Assert.IsGreaterThan(0, logDump);
        StringAssert.Contains(smokeJob[logDump..], "if: always()");

        // The manually-dispatched Aspire mesh lane's container logs are the only lead into a failure like the 2026-09-08
        // SqlException pre-login handshake run, where the sql_check health probe stayed Unhealthy with no
        // other explanation on the runner - the diagnostics step must exist, run only after that lane
        // actually ran and failed, and cover both the sql/mssql containers and host memory pressure.
        var aspireStep = workflow.IndexOf("Aspire Core Lane Mesh Tests (manual)", StringComparison.Ordinal);
        Assert.IsGreaterThan(0, aspireStep);
        StringAssert.Contains(workflow[aspireStep..], "id: aspire_mesh");
        var diagnosticsStep = workflow.IndexOf("Aspire Mesh Diagnostics (on failure)", StringComparison.Ordinal);
        Assert.IsGreaterThan(aspireStep, diagnosticsStep, "the diagnostics step must follow the Aspire Mesh Tests step");
        var aspireMeshBlock = workflow[aspireStep..diagnosticsStep];

        // Aspire tears the graph down before the diagnostics step runs (2026-09-09 proof run: docker ps -a
        // was already empty), so the Aspire Mesh Tests step itself must capture container state WHILE the
        // graph is up: a background poll loop, per-container logs plus a redacted env dump, and per-poll
        // docker port for every container (not only sql ones) to catch a host-port collision or DCP
        // misrouting between the sql container and the Service Bus emulator's mssql sidecar.
        StringAssert.Contains(aspireMeshBlock, "mkdir -p /tmp/aspire-container-logs");
        StringAssert.Contains(aspireMeshBlock, "sleep 10");
        StringAssert.Contains(aspireMeshBlock, "docker logs --since 12s");
        StringAssert.Contains(aspireMeshBlock, "docker port");
        StringAssert.Contains(aspireMeshBlock, "/tmp/aspire-container-logs/ps.log");
        StringAssert.Contains(aspireMeshBlock, "/tmp/aspire-container-logs/env.log");
        StringAssert.Contains(aspireMeshBlock, "sed -E 's/(PASSWORD=).*/\\1<redacted>/'", "captured env vars must redact password values");
        StringAssert.Contains(aspireMeshBlock, "capture_pid=$!");
        StringAssert.Contains(aspireMeshBlock, "trap ");
        StringAssert.Contains(aspireMeshBlock, "kill \"$capture_pid\"");
        StringAssert.Contains(aspireMeshBlock, "exit \"$test_exit\"", "the loop's cleanup must not swallow dotnet test's own exit code");
        // Second safety net (2026-09-09): print captured evidence inside the Aspire step itself on a
        // non-zero exit, so it survives even if a later gate (e.g. the diagnostics step's own if:) misfires.
        StringAssert.Contains(aspireMeshBlock, "test_exit\" -ne 0");
        StringAssert.Contains(aspireMeshBlock, "tail -n 300");

        var diagnosticsBlock = workflow[diagnosticsStep..];
        StringAssert.Contains(diagnosticsBlock, "/tmp/aspire-container-logs");
        StringAssert.Contains(diagnosticsBlock, "tail -n 300");
        // A condition with no status-check function gets an implicit success() ANDed in by GitHub Actions,
        // so steps.aspire_mesh.conclusion == 'failure' alone can never run once the Aspire step has failed
        // (this skipped the step in run 34406418605) - failure() must be explicit. Keyed off the Aspire
        // step's own conclusion, not a re-evaluated copy of its if:, so a skipped/successful mesh step or
        // an earlier unrelated failure still cannot trigger this.
        StringAssert.Contains(diagnosticsBlock, "if: ${{ failure() && steps.aspire_mesh.conclusion == 'failure' }}");
        StringAssert.Contains(diagnosticsBlock, "continue-on-error: true");
        StringAssert.Contains(diagnosticsBlock, "free -m");
        StringAssert.Contains(diagnosticsBlock, "docker ps -a");
        StringAssert.Contains(diagnosticsBlock, "docker logs --tail 200");
        // Non-fatal container-name match: no grep in a pipeline that could fail the step, and it reports
        // when nothing matches instead of silently emitting nothing.
        Assert.IsFalse(diagnosticsBlock.Contains("grep", StringComparison.Ordinal), "diagnostics must not rely on grep exit status");
        StringAssert.Contains(diagnosticsBlock, "docker ps -a --format '{{.Names}}'");
        StringAssert.Contains(diagnosticsBlock, "no sql containers");
    }

    /// <summary>
    /// D-060/D-036 compose invariants: only the edge is exposed, Grafana is loopback-only, app containers restart
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
        StringAssert.Contains(compose, "Hosting__Lane: NonAzure");
        StringAssert.Contains(compose, "Database__Provider: PostgreSql");
        StringAssert.Contains(compose, "Messaging__Provider: RabbitMq");
        StringAssert.Contains(compose, "Storage__Provider: S3");
        StringAssert.Contains(compose, "ReadModel__Provider: ${ReadModel__Provider:-PostgreSqlJsonb}");
        StringAssert.Contains(compose, "Audit__Provider: Relational");
        StringAssert.Contains(compose, "DataProtection__Persistence: Redis");
        StringAssert.Contains(compose, "profiles: [\"mongo\"]");
        StringAssert.Contains(compose, "required: false");
        StringAssert.Contains(ServiceBlock(compose, "caddy").Text, "REACT_UI_DOMAIN: ${REACT_UI_DOMAIN:");
        StringAssert.Contains(ServiceBlock(compose, "caddy").Text, "UNO_UI_DOMAIN: ${UNO_UI_DOMAIN:");
        StringAssert.Contains(ServiceBlock(compose, "caddy").Text, "S3_PUBLIC_DOMAIN: ${S3_PUBLIC_DOMAIN:");

        Assert.IsFalse(compose.Contains("env_file:", StringComparison.Ordinal), "services must receive only explicit settings");
        foreach (var publicService in new[] { "caddy", "gateway", "blazor", "react", "uno" })
        {
            var block = ServiceBlock(compose, publicService).Text;
            foreach (var sensitiveSetting in new[]
            {
                "POSTGRES_PASSWORD", "ConnectionStrings__TaskFlowDbContext", "ConnectionStrings__Redis1",
                "RabbitMq__ConnectionString", "RABBITMQ_DEFAULT_PASS", "Storage__S3__AccessKeyId",
                "Storage__S3__SecretAccessKey", "ConnectionStrings__MongoDb1", "Database__Encryption__"
            })
            {
                Assert.IsFalse(block.Contains(sensitiveSetting, StringComparison.Ordinal), $"{publicService}: {sensitiveSetting}");
            }
        }

        var migrator = ServiceBlock(compose, "migrator").Text;
        foreach (var unrelatedSecret in new[] { "Redis1", "RabbitMq", "Storage__S3", "MongoDb1" })
        {
            Assert.IsFalse(migrator.Contains(unrelatedSecret, StringComparison.Ordinal), $"migrator: {unrelatedSecret}");
        }
        StringAssert.Contains(migrator, "ConnectionStrings__TaskFlowDbContextTrxn");
        StringAssert.Contains(migrator, "Database__Encryption__LocalKeyBase64");

        var redis = ServiceBlock(compose, "redis").Text;
        StringAssert.Contains(redis, "--requirepass");
        StringAssert.Contains(redis, "REDISCLI_AUTH");
        var mongo = ServiceBlock(compose, "mongo").Text;
        StringAssert.Contains(mongo, "MONGO_INITDB_ROOT_USERNAME");
        StringAssert.Contains(mongo, "MONGO_INITDB_ROOT_PASSWORD");
        StringAssert.Contains(mongo, "--authenticationDatabase admin");
        foreach (var internalNetwork in new[] { "app", "data", "cache", "telemetry" })
        {
            Assert.IsTrue(
                System.Text.RegularExpressions.Regex.IsMatch(
                    compose,
                    $"^  {internalNetwork}:\\r?$\\n    driver: bridge\\r?$\\n    internal: true\\r?$",
                    System.Text.RegularExpressions.RegexOptions.Multiline),
                internalNetwork);
        }
        var caddy = ServiceBlock(compose, "caddy").Text;
        StringAssert.Contains(caddy, "      - edge");
        foreach (var internalNetwork in new[] { "app", "data", "cache", "telemetry" })
        {
            Assert.IsFalse(caddy.Contains($"      - {internalNetwork}", StringComparison.Ordinal), internalNetwork);
        }
        var api = ServiceBlock(compose, "api").Text;
        StringAssert.Contains(api, "      - app");
        Assert.IsFalse(api.Contains("      - edge", StringComparison.Ordinal));
        foreach (var frontend in new[] { "gateway", "blazor" })
        {
            var block = ServiceBlock(compose, frontend).Text;
            StringAssert.Contains(block, "      - edge");
            StringAssert.Contains(block, "      - app");
        }

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
        foreach (var appService in new[] { "api", "gateway", "scheduler", "blazor", "migrator", "react", "uno" })
        {
            Assert.IsFalse(
                ServiceBlock(compose, appService).Text.Contains("healthcheck:", StringComparison.Ordinal),
                appService);
        }

        var local = File.ReadAllText(RepoRoot.Combine("deploy", "compose", "docker-compose.override.local.yml"));
        var imageCatalog = File.ReadAllText(RepoRoot.Combine("src", "Shared", "TaskFlow.Hosting", "ContainerImages.cs"));
        foreach (var (repositoryConstant, repository, tagConstant, tag, imageConstant, composeImage) in new[]
        {
            ("PostgreSqlRepository", "pgvector/pgvector", "PostgreSqlTag", "pg18", "PostgreSql", "image: pgvector/pgvector:pg18"),
            ("RabbitMqRepository", "rabbitmq", "RabbitMqTag", "4-management", "RabbitMq", "image: rabbitmq:4-management"),
            ("SeaweedFsRepository", "chrislusf/seaweedfs", "SeaweedFsTag", "latest", "SeaweedFs", "image: chrislusf/seaweedfs:latest"),
            ("MongoDbRepository", "mongo", "MongoDbTag", "8", "MongoDb", "image: mongo:8"),
            ("RedisRepository", "redis", "RedisTag", "8", "Redis", "image: redis:8")
        })
        {
            StringAssert.Contains(imageCatalog, $"public const string {repositoryConstant} = \"{repository}\";");
            StringAssert.Contains(imageCatalog, $"public const string {tagConstant} = \"{tag}\";");
            StringAssert.Contains(imageCatalog, $"public const string {imageConstant} = $\"{{{repositoryConstant}}}:{{{tagConstant}}}\";");
            StringAssert.Contains(compose, composeImage, $"Compose must match ContainerImages '{imageConstant}'.");
        }

        StringAssert.Contains(compose, "pg_isready");
        StringAssert.Contains(compose, "rabbitmq-diagnostics");
        var seaweedfs = ServiceBlock(compose, "seaweedfs").Text;
        StringAssert.Contains(seaweedfs, "command: [\"mini\", \"-dir=/data\"]");
        StringAssert.Contains(seaweedfs, "AWS_ACCESS_KEY_ID: ${Storage__S3__AccessKeyId:");
        StringAssert.Contains(seaweedfs, "AWS_SECRET_ACCESS_KEY: ${Storage__S3__SecretAccessKey:");
        StringAssert.Contains(seaweedfs, "http://127.0.0.1:9333/cluster/healthz");
        var seaweedFixture = File.ReadAllText(
            RepoRoot.Combine("tests", "Test.Integration", "Infrastructure", "SeaweedFsContainerFixture.cs"));
        StringAssert.Contains(seaweedFixture, ".WithCommand(\"mini\", \"-dir=/data\")");
        StringAssert.Contains(seaweedFixture, ".WithEnvironment(\"AWS_ACCESS_KEY_ID\", AccessKey)");
        StringAssert.Contains(seaweedFixture, ".WithEnvironment(\"AWS_SECRET_ACCESS_KEY\", SecretKey)");
        StringAssert.Contains(local, "- nuget_credentials");
        StringAssert.Contains(local, "environment: NuGetPackageSourceCredentials_efreeman518-github");

        var envExample = File.ReadAllText(RepoRoot.Combine("deploy", "compose", ".env.example"));
        foreach (var setting in new[]
        {
            "Hosting__Lane=NonAzure", "Database__Provider=PostgreSql", "Messaging__Provider=RabbitMq",
            "Storage__Provider=S3", "ReadModel__Provider=PostgreSqlJsonb", "Audit__Provider=Relational",
            "Search__Provider=Sql", "AiServices__Provider=None", "DataProtection__Persistence=Redis",
            "Storage__S3__ServiceUrl=http://seaweedfs:8333", "Storage__S3__ForcePathStyle=true"
        })
        {
            StringAssert.Contains(envExample, setting, setting);
        }

        foreach (var name in new[]
        {
            "Database__PostgreSql__PoolerMode", "POSTGRES_DB", "POSTGRES_USER", "POSTGRES_PASSWORD",
            "REDIS_PASSWORD", "MONGO_INITDB_ROOT_USERNAME", "MONGO_INITDB_ROOT_PASSWORD",
            "ConnectionStrings__TaskFlowDbContextTrxn", "ConnectionStrings__Redis1",
            "Messaging__RabbitMq__ConnectionString", "ConnectionStrings__RabbitMq1", "RABBITMQ_DEFAULT_USER",
            "RABBITMQ_DEFAULT_PASS",
            "Storage__S3__PublicServiceUrl", "Storage__S3__AccessKeyId", "Storage__S3__SecretAccessKey",
            "ConnectionStrings__MongoDb1", "Database__Encryption__LocalKeyBase64", "Grpc__TaskFlowRead__Address",
            "OTEL_EXPORTER_OTLP_ENDPOINT", "CADDY_DOMAIN", "ACME_EMAIL", "GATEWAY_BASE_URL",
            "REACT_UI_ORIGIN", "UNO_UI_ORIGIN", "REACT_UI_DOMAIN", "UNO_UI_DOMAIN",
            "S3_PUBLIC_DOMAIN"
        })
        {
            StringAssert.Contains(envExample, $"\n{name}=", name);
        }

        foreach (var name in new[]
        {
            "POSTGRES_PASSWORD", "ConnectionStrings__TaskFlowDbContextTrxn",
            "ConnectionStrings__TaskFlowDbContextQuery", "ConnectionStrings__TaskFlowFlowEngineDbContext",
            "ConnectionStrings__TickerQDbContext", "REDIS_PASSWORD", "ConnectionStrings__Redis1",
            "RABBITMQ_DEFAULT_PASS", "Messaging__RabbitMq__ConnectionString", "ConnectionStrings__RabbitMq1",
            "Storage__S3__AccessKeyId", "Storage__S3__SecretAccessKey", "MONGO_INITDB_ROOT_PASSWORD",
            "Database__Encryption__LocalKeyBase64", "Database__Encryption__BlindIndexKeyBase64"
        })
        {
            var line = envExample.Split('\n').Single(candidate => candidate.StartsWith($"{name}=", StringComparison.Ordinal));
            StringAssert.Contains(line, "CHANGE_ME_", $"{name} must use an obvious non-production value");
        }

        var nonAzureDeployment = compose + envExample + local;
        foreach (var azureSetting in new[] { "AppConfig__", "KeyVault__", "AZURE_", "ServiceBus", "Cosmos", "AzureBlob" })
        {
            Assert.IsFalse(nonAzureDeployment.Contains(azureSetting, StringComparison.Ordinal), azureSetting);
        }
        Assert.IsFalse(nonAzureDeployment.Contains("minio", StringComparison.OrdinalIgnoreCase));

        var gitignore = File.ReadAllText(RepoRoot.Combine(".gitignore"));
        StringAssert.Contains(gitignore, "deploy/compose/.env");
        StringAssert.Contains(gitignore, "deploy/compose/.env.base");
        StringAssert.Contains(gitignore, "deploy/compose/images.env");
    }

    [TestMethod]
    public void AzureBicep_UsesOnlyTheAzureLaneContract()
    {
        var bicep = File.ReadAllText(RepoRoot.Combine("infra", "main.bicep"));
        var functions = File.ReadAllText(RepoRoot.Combine("infra", "modules", "functions.bicep"));

        foreach (var setting in new[]
        {
            "{ name: 'Hosting__Lane', value: 'Azure' }",
            "{ name: 'Database__Provider', value: 'SqlServer' }",
            "{ name: 'Messaging__Provider', value: 'ServiceBus' }",
            "{ name: 'Storage__Provider', value: 'AzureBlob' }",
            "{ name: 'ReadModel__Provider', value: 'Cosmos' }",
            "{ name: 'Audit__Provider', value: 'AzureTable' }",
            "{ name: 'DataProtection__Persistence', value: 'AzureBlob' }"
        })
        {
            StringAssert.Contains(bicep, setting, setting);
            StringAssert.Contains(functions, setting, setting);
        }

        StringAssert.Contains(bicep, "module blazor 'modules/container-app.bicep'");
        StringAssert.Contains(bicep, "module reactStaticWebApp 'modules/static-web-app.bicep'");
        StringAssert.Contains(bicep, "module unoStaticWebApp 'modules/static-web-app.bicep'");
        StringAssert.Contains(bicep, "{ name: 'Gateway__BaseUrl', value: 'https://${gateway.outputs.fqdn}' }");
        StringAssert.Contains(bicep, "{ name: 'Search__Provider', value: searchProvider }");
        StringAssert.Contains(bicep, "{ name: 'ServiceBus1__fullyQualifiedNamespace', value: serviceBus.outputs.namespaceEndpoint }");
        StringAssert.Contains(functions, "{ name: 'ServiceBus1__fullyQualifiedNamespace', value: serviceBusNamespace }");
        StringAssert.Contains(functions, "{ name: 'DomainEventsTopic', value: 'DomainEvents' }");
        Assert.IsFalse(bicep.Contains("SERVICEBUS__fullyQualifiedNamespace", StringComparison.Ordinal));
        Assert.IsFalse(functions.Contains("SERVICEBUS__fullyQualifiedNamespace", StringComparison.Ordinal));

        var functionTableRbac = bicep[bicep.IndexOf("module funcTableContributor", StringComparison.Ordinal)..];
        StringAssert.Contains(functionTableRbac, "principalId: functions.outputs.functionAppPrincipalId");
        StringAssert.Contains(functionTableRbac, "roleDefinitionId: roles.storageTableDataContributor");
        StringAssert.Contains(bicep, "storageTableEndpoint: storage.outputs.appStorageTableEndpoint");
        StringAssert.Contains(functions, "{ name: 'ConnectionStrings__TableStorage1', value: storageTableEndpoint }");
        StringAssert.Contains(functions, "{ name: 'BlobStorage1__blobServiceUri', value: storageBlobEndpoint }");
        StringAssert.Contains(functions, "{ name: 'BlobStorage1__queueServiceUri', value: storageQueueEndpoint }");

        var apiBlock = bicep[
            bicep.IndexOf("module api 'modules/container-app.bicep'", StringComparison.Ordinal)..
            bicep.IndexOf("module scheduler 'modules/container-app.bicep'", StringComparison.Ordinal)];
        StringAssert.Contains(apiBlock, "{ name: 'ConnectionStrings__TableStorage1', value: storage.outputs.appStorageTableEndpoint }");
        var apiTableRbac = bicep[bicep.IndexOf("module apiTableContributor", StringComparison.Ordinal)..];
        StringAssert.Contains(apiTableRbac, "principalId: api.outputs.principalId");
        StringAssert.Contains(apiTableRbac, "roleDefinitionId: roles.storageTableDataContributor");

        var schedulerBlock = bicep[
            bicep.IndexOf("module scheduler 'modules/container-app.bicep'", StringComparison.Ordinal)..
            bicep.IndexOf("module blazor 'modules/container-app.bicep'", StringComparison.Ordinal)];
        StringAssert.Contains(schedulerBlock, "{ name: 'ConnectionStrings__BlobStorage1', value: storage.outputs.appStorageBlobEndpoint }");
        StringAssert.Contains(schedulerBlock, "{ name: 'ConnectionStrings__TableStorage1', value: storage.outputs.appStorageTableEndpoint }");
        var schedulerBlobRbac = bicep[bicep.IndexOf("module schedulerBlobContributor", StringComparison.Ordinal)..];
        StringAssert.Contains(schedulerBlobRbac, "principalId: scheduler.outputs.principalId");
        StringAssert.Contains(schedulerBlobRbac, "roleDefinitionId: roles.storageBlobDataContributor");
        var schedulerTableRbac = bicep[bicep.IndexOf("module schedulerTableContributor", StringComparison.Ordinal)..];
        StringAssert.Contains(schedulerTableRbac, "principalId: scheduler.outputs.principalId");
        StringAssert.Contains(schedulerTableRbac, "roleDefinitionId: roles.storageTableDataContributor");

        foreach (var nonAzureOnlyValue in new[] { "PostgreSql", "RabbitMq", "Storage__S3", "MongoDb" })
        {
            Assert.IsFalse(bicep.Contains(nonAzureOnlyValue, StringComparison.Ordinal), nonAzureOnlyValue);
        }
    }

    [TestMethod]
    public void StaticUiDeployment_UsesOneRuntimeGatewayContract()
    {
        var reactConfig = File.ReadAllText(RepoRoot.Combine("src", "UI", "TaskFlow.React", "src", "api", "runtimeConfig.ts"));
        var unoConfig = File.ReadAllText(RepoRoot.Combine("src", "UI", "TaskFlow.Uno.Core", "Client", "RuntimeGatewayConfiguration.cs"));
        var compose = File.ReadAllText(RepoRoot.Combine("deploy", "compose", "docker-compose.yml"));
        var caddy = File.ReadAllText(RepoRoot.Combine("deploy", "compose", "Caddyfile"));

        StringAssert.Contains(reactConfig, "/app-config.json");
        StringAssert.Contains(unoConfig, "gatewayBaseUrl");
        StringAssert.Contains(compose, "GATEWAY_BASE_URL");
        StringAssert.Contains(compose, "CorsSettings__AllowedOrigins__2");
        StringAssert.Contains(caddy, "reverse_proxy react:8080");
        StringAssert.Contains(caddy, "reverse_proxy uno:8080");

        var reactStaticConfig = File.ReadAllText(
            RepoRoot.Combine("src", "UI", "TaskFlow.React", "public", "staticwebapp.config.json"));
        StringAssert.Contains(reactStaticConfig, "navigationFallback");
        StringAssert.Contains(reactStaticConfig, "/app-config.json");

        foreach (var dockerfile in new[]
        {
            RepoRoot.Combine("src", "UI", "TaskFlow.React", "Dockerfile"),
            RepoRoot.Combine("src", "UI", "TaskFlow.Uno", "Dockerfile")
        })
        {
            var dockerfileText = File.ReadAllText(dockerfile);
            StringAssert.Contains(dockerfileText, "RUN install -d -o 101 -g 101 /var/cache/nginx/app-config");
            StringAssert.Contains(dockerfileText, "COPY --chown=101:101 deploy/compose/static/app-config.json.template /var/cache/nginx/app-config/app-config.json");
            StringAssert.Contains(dockerfileText, "NGINX_ENVSUBST_OUTPUT_DIR=/var/cache/nginx/app-config");
            Assert.IsFalse(dockerfileText.Contains("COPY --from=build --chown=101:101", StringComparison.Ordinal));
            Assert.IsFalse(dockerfileText.Contains("COPY --chown=101:101 deploy/compose/static/default.conf", StringComparison.Ordinal));
            Assert.IsFalse(dockerfileText.Contains("COPY --chown=101:101 deploy/compose/static/app-config.json.template /etc/nginx/templates", StringComparison.Ordinal));
            Assert.IsFalse(dockerfileText.Contains("RUN touch /usr/share/nginx/html/app-config.json", StringComparison.Ordinal));
            StringAssert.Contains(dockerfileText, "USER 101");
        }

        var nginxConfig = File.ReadAllText(RepoRoot.Combine("deploy", "compose", "static", "default.conf"));
        StringAssert.Contains(nginxConfig, "alias /var/cache/nginx/app-config/app-config.json;");

        var bootstrap = File.ReadAllText(RepoRoot.Combine("infra", "scripts", "bootstrap.ps1"));
        StringAssert.Contains(bootstrap, "reactStaticWebAppDefaultHostname");
        StringAssert.Contains(bootstrap, "unoStaticWebAppDefaultHostname");
        Assert.IsFalse(bootstrap.Contains("staticWebAppDefaultHostname", StringComparison.Ordinal));
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
