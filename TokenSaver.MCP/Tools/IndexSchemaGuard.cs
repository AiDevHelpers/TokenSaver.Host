using System.Text.Json;

namespace AiDevTools.TokenSaver.MCP.Tools;

/// <summary>
///     A stored index whose schema is older than the code fails its queries with
///     "no such column". Tools report that as needs_rebuild and ask for a rebuild instead of
///     returning the raw SQL error. Nothing is rebuilt automatically.
/// </summary>
internal static class IndexSchemaGuard
{
    internal const string RebuildHint =
        "The stored semantic index schema is out of date. Ask the user whether to rebuild it, then call IndexRepo with force: true for this root.";

    internal static bool IsSchemaMismatch(Exception ex) =>
        ex.Message.Contains("no such column", StringComparison.OrdinalIgnoreCase);

    internal static string NeedsRebuildJson(string? root, Exception ex) =>
        JsonSerializer.Serialize(new
        {
            type = "semantic",
            root,
            status = "needs_rebuild",
            last_error = ex.Message,
            rebuild = RebuildHint
        });
}
