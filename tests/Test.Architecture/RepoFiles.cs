namespace Test.Architecture;

/// <summary>
/// Repository-root and source-file discovery for the rules that inspect files rather than IL (host csproj
/// properties, the blocking-call sweep). Kept in one place so the two do not each grow their own walk.
/// Pure-unit tier: file reads under the repo, no DI, host, or network.
/// </summary>
internal static class RepoFiles
{
    /// <summary>Absolute path of the repository root.</summary>
    public static string Root { get; } = FindRoot();

    /// <summary>Every hand-authored C# file under <c>src/</c>, excluding build output.</summary>
    public static IReadOnlyList<string> SourceFiles { get; } = EnumerateSourceFiles();

    /// <summary>Path under the repository root, using the platform separator.</summary>
    public static string Path(params string[] segments) =>
        System.IO.Path.Combine([Root, .. segments]);

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            // A regular clone has ".git" as a directory; a git worktree checkout (used by orchestrated
            // refactor sessions) has ".git" as a plain gitdir-pointer file. Either marks the repo root.
            var gitPath = System.IO.Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the repository root above {AppContext.BaseDirectory}.");
    }

    private static IReadOnlyList<string> EnumerateSourceFiles()
    {
        var src = System.IO.Path.Combine(Root, "src");
        return [.. Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            // bin/ and obj/ hold compiler- and generator-emitted code that no rule here governs, and
            // whose contents depend on which configurations happen to have been built.
            .Where(f => !f.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{System.IO.Path.DirectorySeparatorChar}bin{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal))];
    }
}
