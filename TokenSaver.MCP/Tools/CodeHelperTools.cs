using TokenSaver.AgentHelpers.Core.Contracts;
using TokenSaver.AgentHelpers.Core.Models;
using TokenSaver.AgentHelpers.Core.Services;

using TokenSaver.CodeHelper.Indexing;
using TokenSaver.CodeHelper.Models.Index;
using TokenSaver.CodeHelper.Models.Patch;
using TokenSaver.CodeHelper.Models.Planning;
using TokenSaver.CodeHelper.Patching;
using TokenSaver.CodeHelper.Planning;
using TokenSaver.CodeHelper.Research;
using TokenSaver.ContextHelper.Models;
using TokenSaver.ContextHelper.Pipeline;
using TokenSaver.Common.Core.Configuration;
using TokenSaver.MCP.Services;

using Microsoft.Extensions.Options;

using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Globalization;
using System.Text;

namespace TokenSaver.MCP.Tools;

[McpServerToolType]
internal sealed class CodeHelperTools
{
    private readonly ContextCleanerService _cleaner;
    private readonly TokenSaverConfig _config;
    private readonly ICallerDetectionService _detection;
    private readonly PlanExecutor _executor;
    private readonly IHttpContextAccessor _httpContext;
    private readonly WorkspaceIndexService _index;
    private readonly SymbolSearchService _symbolSearch;
    private readonly DependencyTrackingService _dependencies;
    private readonly PatchEngine _patch;
    private readonly PlanBuilder _planBuilder;
    private readonly AgentAdapterRegistry _registry;
    private readonly PlanStore _store;
    private readonly WorkspaceRootSessionResolver _workspaceRootResolver;

    public CodeHelperTools(
        WorkspaceIndexService index,
        SymbolSearchService symbolSearch,
        DependencyTrackingService dependencies,
        PatchEngine patch,
        PlanBuilder planBuilder,
        PlanExecutor executor,
        PlanStore store,
        IOptions<TokenSaverConfig> config,
        ContextCleanerService cleaner,
        ICallerDetectionService detection,
        AgentAdapterRegistry registry,
        IHttpContextAccessor httpContext,
        WorkspaceRootSessionResolver workspaceRootResolver)
    {
        _index = index;
        _symbolSearch = symbolSearch;
        _dependencies = dependencies;
        _patch = patch;
        _planBuilder = planBuilder;
        _executor = executor;
        _store = store;
        _config = config.Value;
        _cleaner = cleaner;
        _detection = detection;
        _registry = registry;
        _httpContext = httpContext;
        _workspaceRootResolver = workspaceRootResolver;
    }

    // ── CodeIndex ─────────────────────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Query the workspace code index. action determines what happens:\n" +
        "  scan         — scan and index all source files (incremental on repeat calls). root optional.\n" +
        "  find         — find a symbol by name. name required; kind and filePath are optional filters.\n" +
        "  dependencies — show what a file imports and what imports it. filePath required; " +
        "direction: 'uses' | 'usedBy' | 'both' (default).")]
    public async Task<string> CodeIndex(
        [Description("Action: 'scan', 'find', or 'dependencies'")]
        string action,
        [Description("Symbol name — required for 'find'")]
        string? name = null,
        [Description("Symbol kind filter for 'find': Class, Interface, Method, Property, Field, Function")]
        string? kind = null,
        [Description("File path — required for 'dependencies'; optional filter for 'find'")]
        string? filePath = null,
        [Description("Dependency direction for 'dependencies': 'uses', 'usedBy', or 'both' (default)")]
        string direction = "both",
        [Description("Workspace root path")] string? root = null,
        McpServer server = null!,
        CancellationToken ct = default)
    {
        await _workspaceRootResolver.EnsureResolvedAsync(server, ct);

        string r = root ?? _config.WorkspaceRoot;
        return action.Trim().ToUpperInvariant() switch
        {
            "SCAN" => await DoScan(r, ct),
            "FIND" => await DoFind(name, kind, filePath, r, ct),
            "DEPENDENCIES" => await DoDeps(filePath, direction, r, ct),
            _ => "Unknown action. Use 'scan', 'find', or 'dependencies'."
        };
    }

    private async Task<string> DoScan(string root, CancellationToken ct)
    {
        IndexStats stats = await _index.IndexWorkspaceAsync(root, ct);
        return $"Index complete: {stats.FilesIndexed} files indexed, {stats.SymbolsIndexed} symbols, " +
               $"{stats.FilesSkipped} unchanged, completed in {stats.Elapsed.TotalSeconds:F1}s.";
    }

    private async Task<string> DoFind(string? name, string? kind, string? filePath, string root, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "name is required for action 'find'.";
        }

        SymbolKind? kindFilter = kind != null && Enum.TryParse<SymbolKind>(kind, true, out SymbolKind k) ? k : null;
        IReadOnlyList<SymbolEntry> symbols = await _symbolSearch.FindSymbolAsync(name, kindFilter, filePath, root, ct);
        if (symbols.Count == 0)
        {
            return $"No symbols named '{name}' found. Run CodeIndex(action: 'scan') first if you haven't yet.";
        }

        StringBuilder sb = new($"Found {symbols.Count} symbol(s) named '{name}':\n\n");
        foreach (SymbolEntry s in symbols)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  {s.Kind,-12} {s.FullName}");
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"               {s.FilePath}:{s.Line}");
            if (s.Summary != null)
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"               // {s.Summary}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private async Task<string> DoDeps(string? filePath, string direction, string root, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return "filePath is required for action 'dependencies'.";
        }

        StringBuilder sb = new();
        if (direction is "both" or "uses")
        {
            IReadOnlyList<string> deps = await _dependencies.GetFileDependenciesAsync(filePath, root, ct);
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"{filePath} uses:");
            if (deps.Count == 0)
            {
                _ = sb.AppendLine("  (none found)");
            }
            else
            {
                foreach (string d in deps)
                {
                    _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  {d}");
                }
            }
        }

        if (direction is "both" or "usedBy")
        {
            IReadOnlyList<string> depBy = await _dependencies.GetFileDependentsAsync(filePath, root, ct);
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"{filePath} is used by:");
            if (depBy.Count == 0)
            {
                _ = sb.AppendLine("  (none found)");
            }
            else
            {
                foreach (string d in depBy)
                {
                    _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  {d}");
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    // ── EditFile ──────────────────────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Apply, insert, delete, rename, or roll back a file edit. A backup is written automatically " +
        "for every mutating action; pass dryRun=true to validate without writing. action determines what happens:\n" +
        "  apply    — replace oldText with newText in filePath.\n" +
        "  insert   — insert content before/after an anchor string in filePath (position = 'before' | 'after').\n" +
        "  delete   — remove text matching pattern from filePath (set isRegex=true for a regex pattern).\n" +
        "  rename   — replace identifier oldName with newName in filePath (wholeWordOnly and replaceInComments control scope).\n" +
        "  rollback — restore filePath from a backup created by a prior edit. backupPath and targetFilePath required.")]
    public async Task<string> EditFile(
        [Description("Action: 'apply', 'insert', 'delete', 'rename', or 'rollback'")]
        string action,
        [Description("Workspace-relative file path to edit (required for all actions except 'rollback')")]
        string? filePath = null,
        [Description("apply: exact text to replace — must exist verbatim in the file")]
        string? oldText = null,
        [Description("apply: replacement text")]
        string? newText = null,
        [Description("apply: which occurrence to replace (0 = all, default; N = the Nth match)")]
        int occurrence = 0,
        [Description("insert: anchor string to insert relative to")]
        string? anchor = null,
        [Description("insert: text to insert")]
        string? content = null,
        [Description("insert: 'before' or 'after' the anchor (default 'after')")]
        string? position = null,
        [Description("delete: text or regex pattern to remove")]
        string? pattern = null,
        [Description("delete: treat pattern as a regex (default false)")]
        bool isRegex = false,
        [Description("rename: identifier to replace")]
        string? oldName = null,
        [Description("rename: replacement identifier")]
        string? newName = null,
        [Description("rename: only match whole words (default true)")]
        bool wholeWordOnly = true,
        [Description("rename: also rename inside comment lines (default false)")]
        bool replaceInComments = false,
        [Description("Human-readable description of the change (optional)")]
        string? description = null,
        [Description("When true, validates but does not write (optional, default false)")]
        bool dryRun = false,
        [Description("Backup file path returned by a prior edit (required for 'rollback')")]
        string? backupPath = null,
        [Description("Workspace-relative path of the file to restore (required for 'rollback')")]
        string? targetFilePath = null,
        [Description("Workspace root path")] string? root = null,
        McpServer server = null!,
        CancellationToken ct = default)
    {
        await _workspaceRootResolver.EnsureResolvedAsync(server, ct);

        string r = root ?? _config.WorkspaceRoot;
        return action.Trim().ToUpperInvariant() switch
        {
            "APPLY" => await DoApply(filePath, oldText, newText, occurrence, description, dryRun, r, ct),
            "INSERT" => await DoInsert(filePath, anchor, content, position, description, dryRun, r, ct),
            "DELETE" => await DoDelete(filePath, pattern, isRegex, description, dryRun, r, ct),
            "RENAME" => await DoRename(filePath, oldName, newName, wholeWordOnly, replaceInComments, description,
                dryRun, r, ct),
            "ROLLBACK" => await DoRollback(backupPath, targetFilePath, r, ct),
            _ => "Unknown action. Use 'apply', 'insert', 'delete', 'rename', or 'rollback'."
        };
    }

    private async Task<string> DoApply(
        string? filePath, string? oldText, string? newText, int occurrence,
        string? description, bool dryRun, string root, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return "filePath is required for action 'apply'.";
        }

        if (oldText is null)
        {
            return "oldText is required for action 'apply'.";
        }

        if (newText is null)
        {
            return "newText is required for action 'apply'.";
        }

        ReplaceOperation op = new()
        {
            FilePath = filePath,
            OldText = oldText,
            NewText = newText,
            Occurrence = occurrence,
            Description = description ?? "EditFile via MCP",
            DryRun = dryRun
        };
        return Format(await _patch.ApplyAsync(op, root, ct), "apply", filePath, dryRun);
    }

    private async Task<string> DoInsert(
        string? filePath, string? anchor, string? content, string? position,
        string? description, bool dryRun, string root, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return "filePath is required for action 'insert'.";
        }

        if (anchor is null)
        {
            return "anchor is required for action 'insert'.";
        }

        if (content is null)
        {
            return "content is required for action 'insert'.";
        }

        InsertPosition? pos = (position ?? "after").Trim().ToUpperInvariant() switch
        {
            "BEFORE" => InsertPosition.Before,
            "AFTER" => InsertPosition.After,
            _ => null
        };
        if (pos is null)
        {
            return "position must be 'before' or 'after'.";
        }

        InsertOperation op = new()
        {
            FilePath = filePath,
            Anchor = anchor,
            Content = content,
            Position = pos.Value,
            Description = description ?? "EditFile insert via MCP",
            DryRun = dryRun
        };
        return Format(await _patch.ApplyAsync(op, root, ct), "insert", filePath, dryRun);
    }

    private async Task<string> DoDelete(
        string? filePath, string? pattern, bool isRegex,
        string? description, bool dryRun, string root, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return "filePath is required for action 'delete'.";
        }

        if (string.IsNullOrEmpty(pattern))
        {
            return "pattern is required for action 'delete'.";
        }

        DeleteOperation op = new()
        {
            FilePath = filePath,
            Pattern = pattern,
            IsRegex = isRegex,
            Description = description ?? "EditFile delete via MCP",
            DryRun = dryRun
        };
        return Format(await _patch.ApplyAsync(op, root, ct), "delete", filePath, dryRun);
    }

    private async Task<string> DoRename(
        string? filePath, string? oldName, string? newName, bool wholeWordOnly, bool replaceInComments,
        string? description, bool dryRun, string root, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return "filePath is required for action 'rename'.";
        }

        if (string.IsNullOrEmpty(oldName))
        {
            return "oldName is required for action 'rename'.";
        }

        if (string.IsNullOrEmpty(newName))
        {
            return "newName is required for action 'rename'.";
        }

        RenameOperation op = new()
        {
            FilePath = filePath,
            OldName = oldName,
            NewName = newName,
            WholeWordOnly = wholeWordOnly,
            ReplaceInComments = replaceInComments,
            Description = description ?? "EditFile rename via MCP",
            DryRun = dryRun
        };
        return Format(await _patch.ApplyAsync(op, root, ct), "rename", filePath, dryRun);
    }

    private static string Format(PatchResult result, string verb, string filePath, bool dryRun)
    {
        return !result.Success
            ? $"❌ Edit failed: {result.Error}"
            : dryRun
            ? $"✓ DryRun: {verb} is valid. ~{result.LinesChanged} line(s) would change in {filePath}."
            : $"✓ Applied ({verb}): {result.LinesChanged} line(s) changed in {filePath}. Backup: {result.BackupPath}";
    }

    private async Task<string> DoRollback(string? backupPath, string? targetFilePath, string root, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
        {
            return "backupPath is required for action 'rollback'.";
        }

        if (string.IsNullOrWhiteSpace(targetFilePath))
        {
            return "targetFilePath is required for action 'rollback'.";
        }

        bool success = await _patch.RollbackAsync(backupPath, targetFilePath, root, ct);
        return success
            ? $"✓ Rolled back {targetFilePath} from {backupPath}."
            : $"❌ Rollback failed: backup not found at {backupPath}.";
    }

    // ── ManagePlan ────────────────────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Create and execute step-by-step code task plans. action determines what happens:\n" +
        "  create  — generate a plan from a natural language prompt. Returns plan ID and step list. " +
        "prompt required.\n" +
        "  execute — run one step of a plan. planId and stepNumber required. " +
        "Provide resolvedContent to answer a step that requested AI input.\n" +
        "  status  — show current status of all steps in a plan. planId required.")]
    public async Task<string> ManagePlan(
        [Description("Action: 'create', 'execute', or 'status'")]
        string action,
        [Description("Natural language task description (required for 'create')")]
        string? prompt = null,
        [Description("Plan ID returned by 'create' (required for 'execute' and 'status')")]
        string? planId = null,
        [Description("Step number to run, 1-based (required for 'execute')")]
        int? stepNumber = null,
        [Description("AI-provided resolution for a step that requested AI input (optional for 'execute')")]
        string? resolvedContent = null,
        [Description("Session identifier (optional for 'create')")]
        string? sessionId = null,
        [Description("Workspace root path")] string? root = null,
        McpServer server = null!,
        CancellationToken ct = default)
    {
        await _workspaceRootResolver.EnsureResolvedAsync(server, ct);

        string r = root ?? _config.WorkspaceRoot;
        return action.Trim().ToUpperInvariant() switch
        {
            "CREATE" => await DoCreatePlan(prompt, sessionId, r, ct),
            "EXECUTE" => await DoExecuteStep(planId, stepNumber, resolvedContent, r, ct),
            "STATUS" => DoGetStatus(planId),
            _ => "Unknown action. Use 'create', 'execute', or 'status'."
        };
    }

    private async Task<string> DoCreatePlan(string? prompt, string? sessionId, string root, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return "prompt is required for action 'create'.";
        }

        PipelineResult cleaned = await _cleaner.CleanContextAsync(prompt, sessionId: sessionId, ct: ct);

        // Resolve caller format so ResearchContextService knows whether to embed or hint
        CallerInfo caller = _httpContext.HttpContext is { } hc ? _detection.Detect(hc) : new CallerInfo();
        (IAgentAdapter? modelAdapter, IAgentAdapter? clientAdapter) = _registry.ResolveBoth(caller);
        ContextFormat agentFormat = modelAdapter.GetFormat(caller).WithClientOverrides(clientAdapter.GetFormat(caller));
        ResearchContextFormat researchFmt = new()
        {
            WillAskForMissingContext = agentFormat.WillAskForMissingContext,
            UseXmlSectionTags = agentFormat.UseXmlSectionTags,
            UseMarkdownHeaders = agentFormat.UseMarkdownHeaders,
            CharBudget = agentFormat.ContextTokenBudget * 4,
            PerFileCharLimit = agentFormat.PerFileTokenLimit * 4
        };

        CodePlan plan = await _planBuilder.BuildPlanAsync(cleaned.ContextText, sessionId ?? "default", root, researchFmt,
            ct);
        _store.Save(plan);

        StringBuilder sb = new();
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Plan ID: {plan.PlanId}  ({plan.TaskKind}, {plan.Steps.Count} steps, overall confidence {plan.OverallConfidence:P0})");
        if (plan.Summary != null)
        {
            _ = sb.AppendLine(plan.Summary);
        }

        _ = sb.AppendLine();

        if (plan.Steps.Count == 0)
        {
            _ = sb.AppendLine(
                "No steps were generated. Try CodeIndex(action: 'scan') first, or provide more detail in the prompt.");
            return sb.ToString().TrimEnd();
        }

        foreach (PlanStep step in plan.Steps)
        {
            string flag = step.RequiresAIHelp ? " ⚠ needs your input" : "";
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  Step {step.StepNumber}: [{step.Confidence:P0}]{flag}");
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"    {step.Description}");
            if (step.FilePath != null)
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"    File: {step.FilePath}");
            }
        }

        _ = sb.AppendLine();
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Use ManagePlan(action: 'execute', planId: \"{plan.PlanId}\", stepNumber: 1) to begin.");
        return sb.ToString().TrimEnd();
    }

    private async Task<string> DoExecuteStep(
        string? planId, int? stepNumber, string? resolvedContent, string root, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(planId))
        {
            return "planId is required for action 'execute'.";
        }

        if (stepNumber is null)
        {
            return "stepNumber is required for action 'execute'.";
        }

        CodePlan? plan = _store.Get(planId);
        if (plan == null)
        {
            return $"Plan '{planId}' not found. Use ManagePlan(action: 'create') first.";
        }

        StepExecutionResult result = await _executor.ExecuteStepAsync(plan, stepNumber.Value, root, resolvedContent, ct);

        if (result.Error != null && !result.NeedsAI)
        {
            return $"❌ Step {stepNumber} failed: {result.Error}";
        }

        if (result.NeedsAI)
        {
            return $"⚠ Step {stepNumber} requires your input:\n\n{result.AIPrompt}";
        }

        PlanStep? next = plan.Steps.FirstOrDefault(s => s.StepNumber > stepNumber);
        string nextMsg = next != null
            ? $"\n\nNext: ManagePlan(action: 'execute', planId: \"{planId}\", stepNumber: {next.StepNumber})"
            : "\n\nAll steps complete.";

        return $"✓ Step {stepNumber} applied: {result.Step.Description}\n" +
               $"  File: {result.Patch?.FilePath ?? "n/a"}, {result.Patch?.LinesChanged ?? 0} line(s) changed." +
               nextMsg;
    }

    private string DoGetStatus(string? planId)
    {
        if (string.IsNullOrWhiteSpace(planId))
        {
            return "planId is required for action 'status'.";
        }

        CodePlan? plan = _store.Get(planId);
        if (plan == null)
        {
            return $"Plan '{planId}' not found.";
        }

        StringBuilder sb = new();
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Plan {plan.PlanId} | {plan.TaskKind} | {plan.Steps.Count} steps | confidence {plan.OverallConfidence:P0}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Created: {plan.CreatedAt:yyyy-MM-dd HH:mm} UTC");
        if (plan.Summary != null)
        {
            _ = sb.AppendLine(plan.Summary);
        }

        _ = sb.AppendLine();

        foreach (PlanStep step in plan.Steps)
        {
            string icon = step.Status switch
            {
                PlanStepStatus.Completed => "✓",
                PlanStepStatus.Failed => "✗",
                PlanStepStatus.AIHandoff => "⚠",
                PlanStepStatus.InProgress => "⏳",
                PlanStepStatus.Skipped => "–",
                _ => "○"
            };
            string flag = step.RequiresAIHelp ? " [needs AI]" : "";
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  {icon} Step {step.StepNumber}{flag}: {step.Description}");
            if (step.FilePath != null)
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"      {step.FilePath} ({step.Confidence:P0} confidence)");
            }
        }

        return sb.ToString().TrimEnd();
    }
}
