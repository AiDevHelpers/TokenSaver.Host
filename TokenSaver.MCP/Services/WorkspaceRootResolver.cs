namespace AiDevTools.TokenSaver.MCP.Services;

/// <summary>
///     Walks up from a starting directory to the nearest repo root (a directory containing a
///     .sln/.slnx file or a .git folder). Used as a fallback source by
///     <see cref="WorkspaceRootSessionResolver" /> when a connecting MCP client doesn't support the
///     roots capability.
/// </summary>
internal static class WorkspaceRootResolver
{
    /// <summary>
    ///     Walks up from <paramref name="searchFrom" /> looking for a repo root. Returns the
    ///     first matching directory's full path, or <c>null</c> if none is found before running
    ///     out of parent directories.
    ///     <para>
    ///     Callers always walk up (not just when a workspace root is unset) because some
    ///     launchers (e.g. the VS Code extension) pass a project sub-folder rather than the
    ///     repo root.
    ///     </para>
    /// </summary>
    public static string? FindRepoRoot(string searchFrom)
    {
        DirectoryInfo? dir = new(searchFrom);
        while (dir is not null)
        {
            if (dir.EnumerateFiles("*.sln").Any() ||
                dir.EnumerateFiles("*.slnx").Any() ||
                dir.GetDirectories(".git").Length > 0)
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
