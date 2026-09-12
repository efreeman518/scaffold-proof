namespace Test.Architecture;

/// <summary>
/// Guards the D-047 runtime profile. The properties in <c>src/Host/TaskFlow.Host.props</c> only reach a host
/// through an explicit <c>&lt;Import&gt;</c>, so a new host - or a csproj rewritten by tooling - can drop the
/// line and still build, deploy and pass every other test while running Workstation GC and JIT-only startup.
/// Nothing else in the suite would notice, which is what makes this worth a test rather than a review note.
/// Pure-unit tier (project-file text inspection): no DI, I/O beyond the repo, or host.
/// </summary>
[TestClass]
[TestCategory("Architecture")]
public class HostRuntimeSettingsTests
{
    private const string PropsRelativePath = "src/Host/TaskFlow.Host.props";

    /// <summary>The deployable hosts, and the relative import path each one needs.</summary>
    private static readonly (string Project, string Import)[] Hosts =
    [
        ("src/Host/TaskFlow.Api/TaskFlow.Api.csproj", @"..\TaskFlow.Host.props"),
        ("src/Host/TaskFlow.Gateway/TaskFlow.Gateway.csproj", @"..\TaskFlow.Host.props"),
        ("src/Host/TaskFlow.Scheduler/TaskFlow.Scheduler.csproj", @"..\TaskFlow.Host.props"),
        ("src/Host/TaskFlow.Functions/TaskFlow.Functions.csproj", @"..\TaskFlow.Host.props"),
        ("src/Host/TaskFlow.DatabaseMigrator/TaskFlow.DatabaseMigrator.csproj", @"..\TaskFlow.Host.props"),
        ("src/UI/TaskFlow.Blazor/TaskFlow.Blazor.csproj", @"..\..\Host\TaskFlow.Host.props")
    ];

    /// <summary>Properties the shared profile must declare, with the value the profile commits to.</summary>
    private static readonly (string Property, string Value)[] RequiredProperties =
    [
        ("ServerGarbageCollection", "true"),
        ("ConcurrentGarbageCollection", "true"),
        ("GarbageCollectionAdaptationMode", "1"),
        ("TieredPGO", "true"),
        ("TieredCompilation", "true"),
        ("InvariantGlobalization", "true"),
        ("UseSystemResourceKeys", "false")
    ];

    /// <summary>Verifies every deployable host opts into the shared runtime profile.</summary>
    [TestMethod]
    public void Given_DeployableHosts_When_ProjectFileRead_Then_ImportsSharedRuntimeProfile()
    {
        foreach (var (project, import) in Hosts)
        {
            var text = ReadRepoFile(project);
            Assert.IsTrue(
                text.Contains($"<Import Project=\"{import}\" />", StringComparison.Ordinal),
                $"{project} does not import {import}, so it silently runs the framework default GC and JIT "
                + "profile instead of the D-047 one.");
        }
    }

    /// <summary>Verifies the shared profile still declares every property the hosts rely on it for.</summary>
    [TestMethod]
    public void Given_SharedRuntimeProfile_When_Read_Then_DeclaresEveryRequiredProperty()
    {
        var props = ReadRepoFile(PropsRelativePath);

        foreach (var (property, value) in RequiredProperties)
        {
            Assert.IsTrue(
                props.Contains($"<{property}>{value}</{property}>", StringComparison.Ordinal),
                $"{PropsRelativePath} no longer sets {property} to {value}. The SDK emits the matching "
                + "runtimeconfig entry only when the property is set, so dropping it reverts every host to "
                + "the framework default.");
        }
    }

    /// <summary>Verifies the per-host overrides that deliberately depart from the shared profile.</summary>
    [TestMethod]
    public void Given_HostsWithDifferentNeeds_When_ProjectFileRead_Then_OverridesFollowTheImport()
    {
        // A run-to-completion job gets no throughput from per-core heaps and GC threads.
        AssertOverrideAfterImport(
            "src/Host/TaskFlow.DatabaseMigrator/TaskFlow.DatabaseMigrator.csproj",
            "<ServerGarbageCollection>false</ServerGarbageCollection>");

        // Culture-rendering hosts need real ICU data, which is why their Dockerfiles use -chiseled-extra.
        AssertOverrideAfterImport(
            "src/UI/TaskFlow.Blazor/TaskFlow.Blazor.csproj",
            "<InvariantGlobalization>false</InvariantGlobalization>");
        AssertOverrideAfterImport(
            "src/Host/TaskFlow.Functions/TaskFlow.Functions.csproj",
            "<InvariantGlobalization>false</InvariantGlobalization>");

        // SqlClient-dependent hosts (D-047): Microsoft.Data.SqlClient throws NotSupportedException under
        // invariant globalization, so these three also turn it off and move to -chiseled-extra.
        AssertOverrideAfterImport(
            "src/Host/TaskFlow.Api/TaskFlow.Api.csproj",
            "<InvariantGlobalization>false</InvariantGlobalization>");
        AssertOverrideAfterImport(
            "src/Host/TaskFlow.Scheduler/TaskFlow.Scheduler.csproj",
            "<InvariantGlobalization>false</InvariantGlobalization>");
        AssertOverrideAfterImport(
            "src/Host/TaskFlow.DatabaseMigrator/TaskFlow.DatabaseMigrator.csproj",
            "<InvariantGlobalization>false</InvariantGlobalization>");
    }

    /// <summary>
    /// Verifies every host that turns invariant globalization off runs on an image that carries ICU. The
    /// pairing is the whole point: InvariantGlobalization=false on a plain chiseled base fails at startup
    /// with "Couldn't find a valid ICU package", which no build or unit test would catch. Two independent
    /// reasons land a host here (see the D-047 comment in TaskFlow.Host.props): rendering user-facing
    /// cultures (Blazor), or depending on Microsoft.Data.SqlClient (Api, Scheduler, DatabaseMigrator,
    /// Functions), which throws NotSupportedException under invariant globalization.
    /// </summary>
    [TestMethod]
    public void Given_InvariantGlobalizationOffHosts_When_DockerfileRead_Then_BaseImageCarriesIcu()
    {
        foreach (var dockerfile in new[]
        {
            "src/UI/TaskFlow.Blazor/Dockerfile",
            "src/Host/TaskFlow.Functions/Dockerfile",
            "src/Host/TaskFlow.Api/Dockerfile",
            "src/Host/TaskFlow.Scheduler/Dockerfile",
            "src/Host/TaskFlow.DatabaseMigrator/Dockerfile"
        })
        {
            var text = ReadRepoFile(dockerfile);
            Assert.IsTrue(
                text.Contains("aspnet:10.0-noble-chiseled-extra", StringComparison.Ordinal),
                $"{dockerfile} must use the -chiseled-extra runtime base: this host sets "
                + "InvariantGlobalization=false and plain chiseled ships no ICU.");
        }
    }

    /// <summary>
    /// Verifies the Api and Gateway publish stages stay RID-specific and ReadyToRun. Dropping -r from the
    /// restore line is the subtle one: the publish then fails in CI only, with a missing-assets error that
    /// reads nothing like the ReadyToRun setting that caused it.
    /// </summary>
    [TestMethod]
    public void Given_EdgeHostDockerfiles_When_Read_Then_PublishReadyToRunForLinuxX64()
    {
        foreach (var dockerfile in new[]
        {
            "src/Host/TaskFlow.Api/Dockerfile",
            "src/Host/TaskFlow.Gateway/Dockerfile"
        })
        {
            var text = ReadRepoFile(dockerfile);
            // Comment lines are excluded: they explain these flags, and counting them as occurrences would
            // let a rewritten command pass on the strength of the comment above it.
            var commands = string.Join('\n', text.Split('\n')
                .Where(l => !l.TrimStart().StartsWith('#')));

            Assert.IsTrue(
                commands.Contains("-p:PublishReadyToRun=true", StringComparison.Ordinal),
                $"{dockerfile} must publish with PublishReadyToRun (D-047).");
            Assert.IsTrue(
                commands.Contains("--self-contained false", StringComparison.Ordinal),
                $"{dockerfile} must stay framework-dependent so the chiseled aspnet base supplies the runtime.");
            Assert.AreEqual(
                2,
                commands.Split("-r linux-x64").Length - 1,
                $"{dockerfile} must pass -r linux-x64 to BOTH restore and publish; a --no-restore publish "
                + "fails with a missing-assets error when the restore was RID-agnostic.");
            // BuildKit secret mount for the private NuGet feed must survive any publish-line edit.
            StringAssert.Contains(commands, "--mount=type=secret,id=nuget_credentials",
                $"{dockerfile} lost the BuildKit nuget credential mount.");
        }
    }

    /// <summary>
    /// Verifies no host pins a GC heap hard limit. D-047 leaves container memory to the runtime's
    /// cgroup-aware sizing plus DATAS; a pinned limit stops following the container app's memory setting
    /// and turns a memory increase into a silent no-op.
    /// </summary>
    [TestMethod]
    public void Given_HostProjectsAndDockerfiles_When_Read_Then_NoGCHeapHardLimitIsPinned()
    {
        var files = Hosts.Select(h => h.Project)
            .Append(PropsRelativePath)
            .Concat(new[]
            {
                "src/Host/TaskFlow.Api/Dockerfile",
                "src/Host/TaskFlow.Gateway/Dockerfile",
                "src/Host/TaskFlow.Scheduler/Dockerfile",
                "src/Host/TaskFlow.Functions/Dockerfile",
                "src/Host/TaskFlow.DatabaseMigrator/Dockerfile",
                "src/UI/TaskFlow.Blazor/Dockerfile"
            });

        foreach (var file in files)
        {
            var text = ReadRepoFile(file);
            var pinned = text.Contains("GCHeapHardLimit", StringComparison.OrdinalIgnoreCase)
                && !text.Contains("DOTNET_GCHeapHardLimit / DOTNET_GCHeapHardLimitPercent is set", StringComparison.Ordinal);
            Assert.IsFalse(pinned,
                $"{file} pins a GC heap hard limit. D-047 relies on the runtime reading the cgroup memory "
                + "limit instead, so the heap follows the container app's memory setting.");
        }
    }

    /// <summary>Project files that reference the OpenAI SDK packages (D-041): must stay deployed dependencies.</summary>
    private static readonly string[] OpenAiReferencingProjects =
    [
        "src/Host/TaskFlow.Api/TaskFlow.Api.csproj",
        "src/Host/TaskFlow.Bootstrapper/TaskFlow.Bootstrapper.csproj",
        "src/Host/TaskFlow.Functions/TaskFlow.Functions.csproj"
    ];

    /// <summary>
    /// Verifies no host keeps OpenAI or Microsoft.Extensions.AI.OpenAI out of its publish output via
    /// PrivateAssets="all" (D-041). P5 made both a deployed dependency in Api and Bootstrapper so the
    /// OpenAICompatible arm's OpenAI.dll reaches the publish output; F1 did the same for Functions. A
    /// regression here silently drops OpenAI.dll from just that host's publish output, which no build or
    /// unit test would catch - only a `dotnet publish` inspection would.
    /// </summary>
    [TestMethod]
    public void Given_OpenAiReferencingProjects_When_Read_Then_NeitherPackageIsPrivateAssets()
    {
        foreach (var project in OpenAiReferencingProjects)
        {
            var text = ReadRepoFile(project);
            foreach (var package in new[] { "OpenAI", "Microsoft.Extensions.AI.OpenAI" })
            {
                Assert.IsFalse(
                    text.Contains($"Include=\"{package}\" PrivateAssets=\"all\"", StringComparison.Ordinal),
                    $"{project} marks {package} PrivateAssets=\"all\", which keeps its dll out of the "
                    + "publish output.");
            }
        }
    }

    /// <summary>
    /// Hosts whose dependency closure includes Microsoft.Data.SqlClient: Api, Scheduler, and Functions
    /// reach TaskFlow.Infrastructure.Data's Microsoft.EntityFrameworkCore.SqlServer package reference
    /// through TaskFlow.Bootstrapper; DatabaseMigrator references TaskFlow.Infrastructure.Data directly.
    /// Gateway and Blazor have neither a Bootstrapper nor an Infrastructure.Data reference.
    /// </summary>
    private static readonly string[] SqlClientDependentHosts =
    [
        "src/Host/TaskFlow.Api/TaskFlow.Api.csproj",
        "src/Host/TaskFlow.Scheduler/TaskFlow.Scheduler.csproj",
        "src/Host/TaskFlow.DatabaseMigrator/TaskFlow.DatabaseMigrator.csproj",
        "src/Host/TaskFlow.Functions/TaskFlow.Functions.csproj"
    ];

    /// <summary>
    /// Verifies no SqlClient-dependent host declares InvariantGlobalization=true (D-047).
    /// Microsoft.Data.SqlClient throws NotSupportedException under invariant globalization, so leaving
    /// the shared profile's default in place - or a rewrite that flips the override back to true - would
    /// build and pass every other test, then fail only when the host actually opens a SqlConnection.
    /// </summary>
    [TestMethod]
    public void Given_SqlClientDependentHosts_When_ProjectFileRead_Then_InvariantGlobalizationIsNotTrue()
    {
        foreach (var project in SqlClientDependentHosts)
        {
            var text = ReadRepoFile(project);
            Assert.IsFalse(
                text.Contains("<InvariantGlobalization>true</InvariantGlobalization>", StringComparison.Ordinal),
                $"{project} depends on Microsoft.Data.SqlClient and must not declare "
                + "InvariantGlobalization=true; SqlClient throws NotSupportedException under invariant "
                + "globalization.");
        }
    }

    /// <summary>Matches an actual middleware call, not the method name appearing in a comment or a message string.</summary>
    private static readonly System.Text.RegularExpressions.Regex UseHttpsRedirectionCall = new(
        @"\.UseHttpsRedirection\s*\(", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Verifies no deployable host calls UseHttpsRedirection. Every deployment lane terminates TLS at the
    /// edge (Container Apps ingress in the Azure lane, Caddy in the portable lane, D-036/D-049), so every
    /// host container only ever serves plain http; a reintroduced redirect would 307 the edge's own
    /// health/proxy probes and, on a host with no locally trusted certificate (as TaskFlow.Blazor was before
    /// this rule), break any client that follows the redirect.
    /// </summary>
    [TestMethod]
    public void Given_HostSourceFiles_When_Read_Then_NoneCallUseHttpsRedirection()
    {
        var hostDirectories = Hosts
            .Select(h => System.IO.Path.GetDirectoryName(RepoFiles.Path(h.Project.Split('/')))!)
            .ToArray();

        foreach (var file in RepoFiles.SourceFiles)
        {
            if (!hostDirectories.Any(dir => file.StartsWith(dir, StringComparison.OrdinalIgnoreCase)))
                continue;

            var lines = File.ReadAllLines(file);
            for (var lineNumber = 0; lineNumber < lines.Length; lineNumber++)
            {
                // A simple per-line strip of anything after "//" is enough to skip comments here; this rule
                // does not need a real C# parser.
                var commentStart = lines[lineNumber].IndexOf("//", StringComparison.Ordinal);
                var codeOnly = commentStart >= 0 ? lines[lineNumber][..commentStart] : lines[lineNumber];

                Assert.IsFalse(
                    UseHttpsRedirectionCall.IsMatch(codeOnly),
                    $"{file}:{lineNumber + 1} calls UseHttpsRedirection, but every lane terminates TLS at "
                    + "the edge (D-036/D-049) and hosts only ever serve plain http.");
            }
        }
    }

    [TestMethod]
    public void Given_FunctionsBackgroundTriggers_When_ServiceDefaultsAdded_Then_HeaderPropagationIsDisabled()
    {
        var program = ReadRepoFile("src/Host/TaskFlow.Functions/Program.cs");

        StringAssert.Contains(program, "AddServiceDefaults(addHeaderPropagation: false)",
            "Functions background triggers have no HTTP request context, so the inherited header propagation "
            + "handler would break worker gRPC calls used to complete or dead-letter Service Bus messages.");
    }

    private static string ReadRepoFile(string relativePath) =>
        File.ReadAllText(RepoFiles.Path(relativePath.Split('/')));

    private static void AssertOverrideAfterImport(string project, string overrideElement)
    {
        var text = ReadRepoFile(project);
        var import = text.IndexOf("<Import Project=", StringComparison.Ordinal);
        var declared = text.IndexOf(overrideElement, StringComparison.Ordinal);

        Assert.IsTrue(declared >= 0, $"{project} must declare {overrideElement}.");
        Assert.IsTrue(import >= 0 && import < declared,
            $"{project} declares {overrideElement} before its <Import>, where the shared profile would "
            + "overwrite it. MSBuild takes the last assignment, so an override has to follow the import.");
    }
}
