using TokenSaver.ContextHelper.Models;

using System.Globalization;
using System.Text;

namespace TokenSaver.MCP.Services;

/// <summary>
///     Generates MCP tool context summaries at three verbosity levels based on
///     the current task type, so the calling AI gets exactly as much detail as useful:
///     Planning   → Full: all tools with descriptions, grouped by category
///     Code/Debug/Refactor/Test/Review → Brief: category names + tool list, no descriptions
///     General/Explain → Mention: single-line reminder that tools exist
/// </summary>
internal sealed class McpContextService
{
    // ── Verbosity level mapping ───────────────────────────────────────────────

    internal enum Verbosity
    {
        Full,
        Brief,
        Mention
    }
    // ── Tool registry ─────────────────────────────────────────────────────────
    // Maintained alongside the tool classes. Each entry: (category, name, short description).

    internal static readonly ToolEntry[] Registry =
    [
        // Context management
        new("Context", "Clean And Chat", "Clean and compress conversation history, then call the local model.", false),
        new("Context", "Build Concise Context", "Build a token-efficient workspace summary for embedding in context.",
            false),
        new("Context", "Context Hygiene", "Inspect and prune the .local-mcp context cache."),

        // Code: index + edit + planning (consolidated)
        new("Code", "Code Index",
            "Scan, search, or analyze file dependencies in the workspace code index. Actions: scan | find | dependencies."),
        new("Code", "Edit File", "Apply or roll back a file edit with automatic backup. Actions: apply | rollback."),
        new("Code", "Manage Plan",
            "Create and execute step-by-step code task plans. Actions: create | execute | status."),

        // Auto-workflow + calibration (consolidated)
        new("Auto-Workflow", "Code Work",
            "Run the collaborative auto-workflow for code-build requests. Actions: start | continue.", false),
        new("Auto-Workflow", "Calibration",
            "Manage auto-workflow calibration weights and toggle. Actions: toggle | stats | feedback."),

        // Diagnostics (consolidated)
        new("Diagnostics", "Agent Config",
            "Manage SimpleModelServer agent config and run accuracy tests. Actions: test | get | update | save."),
        new("Diagnostics", "Context Diag",
            "Inspect context pipeline state, collapsed blocks, and .gitignore health. Actions: describe | block | stats | gitignore | gitignore-add."),
        new("Diagnostics", "Log Channels",
            "List log channels the helper can read (files, VS Code, Visual Studio, debug sessions, system logs)."),
        new("Diagnostics", "Log Query",
            "Read a log channel as a filtered, deduplicated summary within maxTokens. Filters: match, exclude, minLevel, category; pass the returned cursor as since."),

        // Provider registration
        new("Setup", "Register Mcp Providers",
            "Detect and register this MCP server in all installed AI client configs."),

        // Session snapshot/restore (Context category, alongside Clean And Chat/Build Concise Context)
        new("Context", "Session Snapshot", "Save a session context blob to disk, distilling it first if large."),
        new("Context", "Session Restore", "Restore a saved session snapshot — distilled summary by default, full blob on request."),

        // Token economy: measurement + model routing + savings history
        new("Token Economy", "Token Budget", "Estimate token cost of content and whether it exceeds a threshold. No model call."),
        new("Token Economy", "Model Route", "Call a local Ollama model chosen by task_type (code | chat | summarize | embed | rerank)."),
        new("Token Economy", "Token Savings Report", "Ranked rollup of recorded token savings by tool or role, with a worth-it verdict per row."),
        new("Token Economy", "Worth Keeping", "Direct yes/no/unknown verdict for one tool/role's distillation savings."),

        // Dev-loop shell-out tools
        new("Dev Loop", "Run Tests", "Run the test suite; distills a failure to which tests failed + likely fix location."),
        new("Dev Loop", "Lint Fix", "Run a linter and distill results grouped by rule."),
        new("Dev Loop", "Build Watch", "Read new build-log lines since a line number, distilling errors if the delta is large."),
        new("Dev Loop", "Container Logs", "Fetch a Docker container's recent logs, distilled to new/anomalous errors."),
        new("Dev Loop", "Diff Summarize", "Summarize a git diff as a commit message instead of returning raw diff text."),

        // Chunk-level semantic index/search (distinct from Code Index's exact/substring symbol search)
        new("Semantic Index", "Index Repo", "Chunk the workspace into function/class/section pieces and summarize+embed each new/changed one."),
        new("Semantic Index", "Semantic Grep", "Natural-language search over the chunk index built by Index Repo.")
    ];

    public static Verbosity VerbosityFor(TaskType taskType)
    {
        return taskType switch
        {
            TaskType.Planning => Verbosity.Full,
            TaskType.Code or TaskType.Debug or TaskType.Refactor
                or TaskType.Test or TaskType.Review => Verbosity.Brief,
            _ => Verbosity.Mention
        };
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    ///     Returns the MCP context block to append to the cleaned context for a given task type.
    ///     Returns an empty string if there is nothing worth adding (shouldn't happen in practice).
    /// </summary>
    public string GetContextBlock(TaskType taskType)
    {
        Verbosity verbosity = VerbosityFor(taskType);
        return verbosity switch
        {
            Verbosity.Full => BuildFull(),
            Verbosity.Brief => BuildBrief(taskType),
            Verbosity.Mention => BuildMention(),
            _ => ""
        };
    }

    // ── Full (Planning) ───────────────────────────────────────────────────────

    private static string BuildFull()
    {
        StringBuilder sb = new();
        _ = sb.AppendLine("## Available MCP Tools");
        _ = sb.AppendLine();
        _ = sb.AppendLine("Use these tools to implement the plan steps below.");
        _ = sb.AppendLine();

        foreach ((string? category, List<ToolEntry>? entries) in GroupedByCategory())
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"### {category}");
            foreach (ToolEntry e in entries)
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"- **{e.Name}** — {e.Description}");
            }

            _ = sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    // ── Brief (Code / Debug / Refactor / Test / Review) ──────────────────────

    private static string BuildBrief(TaskType taskType)
    {
        // Filter to categories relevant for the task type
        HashSet<string> relevantCategories = RelevantCategoriesFor(taskType);
        List<(string Key, List<ToolEntry> Entries)> groups = GroupedByCategory()
            .Where(g => relevantCategories.Contains(g.Key))
            .ToList();

        if (groups.Count == 0)
        {
            return BuildMention();
        }

        StringBuilder sb = new();
        _ = sb.AppendLine("<!-- MCP tools available (may be useful for this task):");

        foreach ((string? category, List<ToolEntry>? entries) in groups)
        {
            string names = string.Join(", ", entries.Select(e => e.Name));
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  {category}: {names}");
        }

        _ = sb.AppendLine("Ask for details on any tool if needed. -->");
        return sb.ToString().TrimEnd();
    }

    // ── Mention (General / Explain) ───────────────────────────────────────────

    private static string BuildMention()
    {
        return "<!-- MCP servers attached. Ask for available MCP tools if needed. -->";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IEnumerable<(string Key, List<ToolEntry> Entries)> GroupedByCategory()
    {
        return Registry
            .GroupBy(e => e.Category)
            .Select(g => (g.Key, g.ToList()));
    }

    private static HashSet<string> RelevantCategoriesFor(TaskType taskType)
    {
        return taskType switch
        {
            TaskType.Code => ["Code", "Auto-Workflow"],
            TaskType.Debug => ["Code", "Diagnostics"],
            TaskType.Refactor => ["Code", "Auto-Workflow"],
            TaskType.Test => ["Code", "Diagnostics"],
            TaskType.Review => ["Code", "Diagnostics"],
            _ => ["Code"]
        };
    }

    internal sealed record ToolEntry(string Category, string Name, string Description, bool ShowInHelp = true);
}
