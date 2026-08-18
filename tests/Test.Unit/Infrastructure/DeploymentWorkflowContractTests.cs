namespace Test.Unit.Infrastructure;

/// <summary>Locks the checked deployment ordering and immutable rollback contract.</summary>
[TestClass]
public sealed class DeploymentWorkflowContractTests
{
    [TestMethod]
    public void DeployWorkflow_PreservesSafeReleaseContract()
    {
        var workflow = File.ReadAllText(Path.Combine(FindRepoRoot(), ".github", "workflows", "deploy.yml"));

        StringAssert.Contains(workflow, "workflow_call:");
        StringAssert.Contains(workflow, "options: [deploy, rollback]");
        StringAssert.Contains(workflow, "commit_sha:");
        StringAssert.Contains(workflow, "cancel-in-progress: false");
        StringAssert.Contains(workflow, "@${digest}");
        StringAssert.Contains(workflow, "artifact-id");
        StringAssert.Contains(workflow, "artifact-digest");
        StringAssert.Contains(workflow, "sha256sum --check --status");
        StringAssert.Contains(workflow, "previousManifestArtifactId");
        StringAssert.Contains(workflow, "Invoke-DeploymentSmoke.ps1");
        StringAssert.Contains(workflow, "--secret id=nuget_credentials");
        Assert.IsFalse(workflow.Contains("--build-arg", StringComparison.Ordinal));
        Assert.IsFalse(workflow.Contains("dotnet nuget update", StringComparison.Ordinal));
        Assert.IsFalse(workflow.Contains("--query '[0].name'", StringComparison.Ordinal));
        StringAssert.Contains(workflow, "Test-ReleaseManifest.ps1 -Path current/release-manifest.json");
        StringAssert.Contains(workflow, "Expected exactly one Function App matching");
        StringAssert.Contains(workflow, "expected_swa=\"${RESOURCE_PREFIX}-${ENVIRONMENT_NAME}-uno\"");

        var ciWorkflow = File.ReadAllText(Path.Combine(FindRepoRoot(), ".github", "workflows", "ci.yml"));
        StringAssert.Contains(ciWorkflow, "NuGetPackageSourceCredentials_efreeman518-github");
        Assert.IsFalse(ciWorkflow.Contains("dotnet nuget update", StringComparison.Ordinal));

        var migration = workflow.IndexOf("  run-migrations:", StringComparison.Ordinal);
        var activation = workflow.IndexOf("  activate-release:", StringComparison.Ordinal);
        Assert.IsGreaterThan(0, migration);
        Assert.IsGreaterThan(migration, activation, "New runtime activation must remain after the migration job.");

        var rollback = workflow[workflow.IndexOf("  rollback:", StringComparison.Ordinal)..];
        Assert.IsFalse(rollback.Contains("docker build", StringComparison.Ordinal));
        Assert.IsFalse(rollback.Contains("dotnet publish", StringComparison.Ordinal));

        var smoke = File.ReadAllText(Path.Combine(FindRepoRoot(), "infra", "scripts", "Invoke-DeploymentSmoke.ps1"));
        StringAssert.Contains(smoke, "/health/full");
        StringAssert.Contains(smoke, "finally");
        StringAssert.Contains(smoke, "-Method Delete");

        var manifestValidator = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "infra", "scripts", "Test-ReleaseManifest.ps1"));
        StringAssert.Contains(manifestValidator, "'^[0-9a-f]{64}$'");

        foreach (var dockerfile in new[]
        {
            "src/Host/TaskFlow.Gateway/Dockerfile",
            "src/Host/TaskFlow.Api/Dockerfile",
            "src/Host/TaskFlow.Scheduler/Dockerfile",
            "src/Host/TaskFlow.DatabaseMigrator/Dockerfile",
            "src/Host/TaskFlow.Functions/Dockerfile",
            "src/UI/TaskFlow.Blazor/Dockerfile"
        })
        {
            var content = File.ReadAllText(Path.Combine(FindRepoRoot(), dockerfile));
            StringAssert.Contains(content, "--mount=type=secret,id=nuget_credentials,required=true");
            StringAssert.Contains(content, "FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime");
            StringAssert.Contains(content, "USER $APP_UID");
            Assert.IsFalse(content.Contains("ARG NUGET_TOKEN", StringComparison.Ordinal));
            Assert.IsFalse(content.Contains("store-password-in-clear-text", StringComparison.Ordinal));
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
