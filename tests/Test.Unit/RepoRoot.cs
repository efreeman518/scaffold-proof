using EF.Testing.Environment;

namespace Test.Unit;

/// <summary>Repository-root paths for tests that assert on committed files rather than on code.</summary>
internal static class RepoRoot
{
    /// <summary>Absolute path of the repository root (a <c>.git</c> directory, or a worktree's <c>.git</c> file).</summary>
    public static string Path { get; } = RepositoryRoot.Find();

    /// <summary>Path under the repository root, using the platform separator.</summary>
    public static string Combine(params string[] segments) =>
        System.IO.Path.Combine([Path, .. segments]);
}
