using Test.Benchmarks;

namespace Test.Endpoints;

/// <summary>
/// Runnable check for the benchmark host: <see cref="ApplicationStyleBenchmarks"/> is never run by the MSTest
/// matrix, so without this a host-setup regression (a lane setting the API now requires) only surfaces when someone
/// runs BenchmarkDotNet. Runs the real GlobalSetup (host boot on the default NonAzure lane, in-memory database,
/// 20 seeded tasks over HTTP), more invocations of each benchmark than the default rate-limit tier allows in its
/// window, and GlobalCleanup, for both application styles.
/// Not parallelized: GlobalSetup sets process-wide environment variables the other endpoint factories read.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class BenchmarkHostSetupTests
{
    /// <summary>Invocations per benchmark: with the seed requests, past the standard tier's 100 per minute.</summary>
    private const int Invocations = 60;

    [TestCategory("Endpoint")]
    [TestMethod]
    [DataRow("Service")]
    [DataRow("Cqrs")]
    public async Task Given_ApplicationStyleBenchmarks_When_SetupRuns_Then_EveryBenchmarkSucceeds(string style)
    {
        var benchmarks = new ApplicationStyleBenchmarks { Style = style };
        benchmarks.Setup();
        try
        {
            for (var i = 0; i < Invocations; i++)
            {
                await benchmarks.SearchTaskItemsAsync();
                await benchmarks.CreateTaskItemAsync();
            }
        }
        finally
        {
            benchmarks.Cleanup();
        }
    }
}
