using Microsoft.Extensions.Logging;

using System.Text.Json;
using System.Text.Json.Nodes;

namespace TokenSaver.MCP.Services;

/// <summary>
///     Additively merges resolved absolute CLAUDE.md paths into Claude Code's own
///     <c>claudeMdExcludes</c> setting (<c>.claude/settings.local.json</c> — local-only, never the
///     shared <c>.claude/settings.json</c>) so it stops loading files whose content has already
///     been folded into the workspace's combined, regenerated <c>CLAUDE.md</c> — see
///     <see cref="InstructionOptimizerService" />'s <c>SyncClaudeCodeCombinedFile</c>. Never removes
///     an existing array entry (including ones this feature didn't add) and never touches any
///     other key already present in the file.
/// </summary>
public static partial class ClaudeMdExcludesWriter
{
    private const string ExcludesKey = "claudeMdExcludes";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to update claudeMdExcludes in {Path}")]
    private static partial void LogWriteFailed(ILogger logger, string path, Exception ex);

    /// <summary>
    ///     Ensures every path in <paramref name="pathsToExclude" /> is present in
    ///     <c>.claude/settings.local.json</c>'s <c>claudeMdExcludes</c> array for
    ///     <paramref name="workspaceRoot" />. Best-effort — an I/O or parse failure is logged and
    ///     swallowed, matching every other step in this pipeline.
    /// </summary>
    public static void EnsureExcluded(string workspaceRoot, IReadOnlyList<string> pathsToExclude, ILogger logger)
    {
        if (pathsToExclude.Count == 0)
        {
            return;
        }

        string settingsPath = Path.Combine(workspaceRoot, ".claude", "settings.local.json");

        try
        {
            JsonObject root = File.Exists(settingsPath)
                ? JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject ?? new JsonObject()
                : new JsonObject();

            JsonArray? excludes = root[ExcludesKey] as JsonArray;
            bool isNewArray = excludes is null;
            excludes ??= [];

            HashSet<string> existing = new(StringComparer.OrdinalIgnoreCase);
            foreach (JsonNode? node in excludes)
            {
                if (node is JsonValue value && value.TryGetValue(out string? s) && s.Length > 0)
                {
                    existing.Add(s);
                }
            }

            bool changed = false;
            foreach (string path in pathsToExclude)
            {
                if (existing.Add(path))
                {
                    excludes.Add(path);
                    changed = true;
                }
            }

            if (!changed)
            {
                return;
            }

            if (isNewArray)
            {
                root[ExcludesKey] = excludes;
            }

            string? dir = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(settingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LogWriteFailed(logger, settingsPath, ex);
        }
    }
}
