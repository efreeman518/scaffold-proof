namespace Test.Unit;

/// <summary>Locates the repository root for tests that assert on committed files rather than on code.</summary>
internal static class RepoRoot
{
    /// <summary>Absolute path of the repository root.</summary>
    public static string Path { get; } = Find();

    /// <summary>Path under the repository root, using the platform separator.</summary>
    public static string Combine(params string[] segments) =>
        System.IO.Path.Combine([Path, .. segments]);

    private static string Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            // A regular clone has ".git" as a directory; a git worktree checkout (used by orchestrated
            // refactor sessions) has ".git" as a plain gitdir-pointer file. Either marks the repo root.
            var gitPath = System.IO.Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
