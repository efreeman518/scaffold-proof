using EF.Testing.Environment;

namespace Test.Architecture;

/// <summary>
/// Repository-root and source-file discovery for the rules that inspect files rather than IL (host csproj
/// properties, the blocking-call sweep). Kept in one place so the two do not each grow their own walk.
/// Pure-unit tier: file reads under the repo, no DI, host, or network.
/// </summary>
internal static class RepoFiles
{
    /// <summary>Absolute path of the repository root (a <c>.git</c> directory, or a worktree's <c>.git</c> file).</summary>
    public static string Root { get; } = RepositoryRoot.Find();

    /// <summary>Every hand-authored C# file under <c>src/</c>; bin/ and obj/ build output is excluded.</summary>
    public static IReadOnlyList<string> SourceFiles { get; } =
        EF.Testing.Architecture.SourceFiles.Enumerate(System.IO.Path.Combine(Root, "src"));

    /// <summary>Path under the repository root, using the platform separator.</summary>
    public static string Path(params string[] segments) =>
        System.IO.Path.Combine([Root, .. segments]);
}
