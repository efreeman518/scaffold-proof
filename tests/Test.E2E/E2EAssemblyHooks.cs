namespace Test.E2E;

/// <summary>
/// Assembly-level lifecycle for the shared database container. It is torn down here rather than in a
/// class cleanup because Testcontainers cannot restart a disposed container: the first class to finish
/// would otherwise dispose the container every later class still needs.
/// </summary>
[TestClass]
public static class E2EAssemblyHooks
{
    /// <summary>Stops the shared database container after every test class has run.</summary>
    [AssemblyCleanup]
    public static Task AssemblyCleanup() => DbApiFactory.StopContainerAsync();
}
