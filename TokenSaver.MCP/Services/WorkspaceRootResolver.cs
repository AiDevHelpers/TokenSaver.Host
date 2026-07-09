using System.Text.Json;
using System.Text.Json.Serialization;

namespace TokenSaver.MCP.Services;

/// <summary>
///     Auto-detects the workspace root by walking up from a starting directory to the nearest
///     repo root (a directory containing a .sln/.slnx file or a .git folder), and — when that
///     walk finds nothing, as always happens for the Windows service (the SCM starts it with cwd
///     <c>C:\Windows\System32</c>, no other signal available) — falls back to whatever a previous,
///     cwd-aware launch persisted.
///     <para>
///     That persisted value lives in its own small file
///     (<c>%ProgramData%\TokenSaver\workspace-root.json</c>), deliberately separate from the
///     user-facing <c>config.json</c> behavior settings: it's an internal bootstrap cache, not a
///     tunable setting, and it has no place in a per-workspace <c>.tokensaver/config.json</c> at
///     all — that file's own location on disk already *is* the workspace root, restating it there
///     would just be redundant.
///     </para>
/// </summary>
internal static class WorkspaceRootResolver
{
    private const string BootstrapFileName = "workspace-root.json";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private static string BootstrapFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TokenSaver", BootstrapFileName);

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

    /// <summary>
    ///     Reads the last resolved workspace root from the bootstrap cache, or <see langword="null" />
    ///     if it's never been written.
    /// </summary>
    public static string? ReadPersistedWorkspaceRoot()
    {
        string path = BootstrapFilePath;
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            string json = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<BootstrapFile>(json, ReadOptions)?.WorkspaceRoot;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Persists a successfully-resolved repo root to the bootstrap cache so a later launch
    ///     with no cwd signal of its own (the Windows service) can recover it. No-op if the value
    ///     hasn't changed.
    /// </summary>
    public static void PersistWorkspaceRoot(string resolvedRoot)
    {
        if (ReadPersistedWorkspaceRoot() == resolvedRoot)
        {
            return;
        }

        try
        {
            string path = BootstrapFilePath;
            _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new BootstrapFile(resolvedRoot), WriteOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: a failed write here just means the next service start falls back to
            // ContentRootPath again, same as before this persistence existed.
        }
    }

    private sealed record BootstrapFile([property: JsonPropertyName("workspaceRoot")] string? WorkspaceRoot);
}
