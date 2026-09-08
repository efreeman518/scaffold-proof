using System.Reflection;
using System.Text.Json;
using TaskFlow.Application.Contracts.Services;

namespace Test.Architecture;

/// <summary>
/// D-054 rules for the internal gRPC read surface.
///
/// The failure this guards against is a second read path. gRPC makes it easy to hand a service a
/// repository or a DbContext and answer "faster", and the moment that happens the REST answer and the
/// gRPC answer are two implementations of the same question that will diverge. The service is allowed to
/// be a transport adapter over the application services and nothing else, and it is allowed to exist in
/// exactly one host - the Gateway keeps REST for public clients.
///
/// The transport's own shape (a dedicated cleartext HTTP/2 port declared in configuration rather than in
/// Program.cs) is checked here too, because nothing else in the suite can: Kestrel binds those endpoints
/// at startup, and no test tier that runs on a build agent starts this host on a real socket.
/// Pure-unit tier: reflection plus repository file reads, no DI and no host.
/// </summary>
[TestClass]
[TestCategory("Architecture")]
public class GrpcArchitectureTests : BaseTest
{
    private const string ServiceTypeName = "TaskFlow.Api.Grpc.TaskFlowReadGrpcService";
    private const string ApiAppSettings = "src/Host/TaskFlow.Api/appsettings.json";
    private const string ApiDockerfile = "src/Host/TaskFlow.Api/Dockerfile";

    private static readonly Assembly ApiHostAssembly = typeof(TaskFlow.Api.RegisterApiServices).Assembly;

    /// <summary>Types no host adapter may take: data access belongs behind the application services.</summary>
    private static readonly string[] ForbiddenDependencyMarkers = ["Repository", "DbContext", "DbSet"];

    /// <summary>The service exists, in the Api host, and derives from the generated server base.</summary>
    [TestMethod]
    public void Given_GrpcReadService_When_Located_Then_ItLivesInTheApiHost()
    {
        var service = ApiHostAssembly.GetType(ServiceTypeName);

        Assert.IsNotNull(service, $"{ServiceTypeName} was not found in {ApiHostAssembly.GetName().Name}.");
        Assert.AreEqual(
            "TaskFlow.Contracts.Grpc.TaskFlowRead+TaskFlowReadBase",
            service!.BaseType?.FullName,
            "the service must derive from the generated server base, not hand-roll the contract.");
    }

    /// <summary>The service takes the shared read service and no data-access dependency.</summary>
    [TestMethod]
    public void Given_GrpcReadService_When_ConstructorInspected_Then_ItDependsOnTheApplicationServices()
    {
        var parameters = ApiHostAssembly.GetType(ServiceTypeName)!
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .SelectMany(c => c.GetParameters())
            .ToArray();

        Assert.Contains(p => p.ParameterType == typeof(ITaskFlowReadService), parameters,
            "the aggregate reads must come from the same service the REST endpoints use.");

        var offenders = parameters
            .Where(p => ForbiddenDependencyMarkers.Any(marker =>
                p.ParameterType.Name.Contains(marker, StringComparison.Ordinal)))
            .Select(p => $"{p.Name}: {p.ParameterType.Name}")
            .ToList();

        Assert.AreEqual(0, offenders.Count,
            $"the gRPC service reaches past the application services: {string.Join(", ", offenders)}");
    }

    /// <summary>Only the Api host implements or maps the gRPC read service.</summary>
    [TestMethod]
    public void Given_RepositorySources_When_Scanned_Then_OnlyTheApiHostServesGrpc()
    {
        var apiHostPath = $"{Path.DirectorySeparatorChar}TaskFlow.Api{Path.DirectorySeparatorChar}";

        var offenders = RepoFiles.SourceFiles
            .Where(file =>
            {
                var text = File.ReadAllText(file);
                return text.Contains("TaskFlowReadBase", StringComparison.Ordinal)
                    || text.Contains("MapGrpcService", StringComparison.Ordinal)
                    || text.Contains("AddGrpc(", StringComparison.Ordinal);
            })
            .Where(file => !file.Contains(apiHostPath, StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(RepoFiles.Root, file))
            .ToList();

        Assert.AreEqual(0, offenders.Count,
            $"gRPC server wiring outside the Api host: {string.Join(", ", offenders)}");
    }

    /// <summary>
    /// The gRPC listener is its own endpoint and declares HTTP/2. Kestrel cannot serve h2c and HTTP/1.1
    /// on one port, so a Protocols value that drifted back to the default would leave every gRPC call
    /// failing the protocol negotiation at runtime with nothing failing at build time.
    /// </summary>
    [TestMethod]
    public void Given_ApiConfiguration_When_KestrelEndpointsRead_Then_GrpcIsASeparateHttp2Port()
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(RepoFiles.Path(ApiAppSettings.Split('/'))));
        var endpoints = settings.RootElement.GetProperty("Kestrel").GetProperty("Endpoints");

        Assert.AreEqual("http://+:8080", endpoints.GetProperty("Http").GetProperty("Url").GetString());

        var grpc = endpoints.GetProperty("Grpc");
        Assert.AreEqual("http://+:8081", grpc.GetProperty("Url").GetString());
        Assert.AreEqual("Http2", grpc.GetProperty("Protocols").GetString());
    }

    /// <summary>
    /// The image exposes both ports and sets no ASPNETCORE_URLS. With both present Kestrel binds the
    /// configured endpoints and warns that it is overriding the addresses, so the variable would be a
    /// misleading second answer to "which ports does this host listen on".
    /// </summary>
    [TestMethod]
    public void Given_ApiDockerfile_When_Read_Then_ConfigurationOwnsTheListeningPorts()
    {
        var lines = File.ReadAllLines(RepoFiles.Path(ApiDockerfile.Split('/')));

        // Directives only. A comment is free to name the variable - explaining why it is absent is the
        // point of the one in the Dockerfile.
        var offenders = lines
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("ENV", StringComparison.OrdinalIgnoreCase)
                        && line.Contains("ASPNETCORE_URLS", StringComparison.Ordinal))
            .ToList();

        Assert.AreEqual(0, offenders.Count,
            "Kestrel:Endpoints in appsettings is the single source of listening addresses for this host, "
            + $"but the image also sets: {string.Join(", ", offenders)}");
        Assert.Contains(line => line.Trim() == "EXPOSE 8080", lines);
        Assert.Contains(line => line.Trim() == "EXPOSE 8081", lines);
    }
}
