using TokenSaver.CodeHelper.Indexing;
using TokenSaver.Config;
using TokenSaver.ContextHelper.Calibration;
using TokenSaver.ContextHelper.Models.Calibration;
using TokenSaver.ContextHelper.Models.Diagnostics;
using TokenSaver.ContextHelper.Pipeline;
using TokenSaver.Library.Services;
using TokenSaver.MCP.Services;

using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Globalization;
using System.Text;

namespace TokenSaver.MCP.Tools;

[McpServerToolType]
internal sealed class ContextDiagnosticsTools(
    ContextCleanerService cleaner,
    ContextPipelineService contextPipeline,
    FolderTreeManager folderTree,
    WorkspaceConfigManager workspaceConfig,
    WorkspaceRootSessionResolver workspaceRootResolver,
    RetrievalCalibrationService retrievalCalibration)
{
    // ── ContextDiag ───────────────────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Inspect the context pipeline state and workspace health. action determines what happens:\n" +
        "  describe         — show what is in the context window with token counts and relevance scores. " +
        "Optional filter: 'code', 'logs', 'files', 'conversation', 'toolcalls', or a file path substring.\n" +
        "  block            — retrieve the full content of a code block that was collapsed during context processing. " +
        "id required (sha256 prefix from a '<code_block id=sha256:...>' placeholder).\n" +
        "  stats            — show per-stage pipeline statistics: input/output tokens, savings %, items dropped/collapsed, elapsed ms.\n" +
        "  file             — fetch the indexed (compressed) content of a file from the local cache. " +
        "id required: workspace-relative path (e.g. 'docs/guide.md').\n" +
        "  index            — fetch the folder index JSON from the local cache. " +
        "id optional: workspace-relative folder path; omit or use '.' for the root index.\n" +
        "  gitignore        — scan the workspace for paths that exist but are not covered by .gitignore, " +
        "including known generated dirs AND heuristically detected caches/build artifacts. " +
        "Grouped by: confirmed issues (known patterns) and suspicious (heuristic, show confidence %).\n" +
        "  gitignore-add    — add all missing known + suspicious-gitignore entries to .gitignore. " +
        "Requires confirmed=true.\n" +
        "  context-exclude  — append paths to .tokensaver/.ignore-additional (gitignore syntax) so they " +
        "are skipped during context building and code indexing, WITHOUT adding them to .gitignore. " +
        "Requires confirmed=true and a paths[] array of workspace-relative directories.\n" +
        "  calibration      — show the retrieval self-tuning state: how far the semantic relevance " +
        "threshold has been auto-adjusted from narrow-filter misses (files requested after the fact " +
        "that scored below the bar applied when context was first built), and the sample/failure counts behind it.")]
    public async Task<string> ContextDiag(
        [Description(
            "Action: 'describe', 'block', 'stats', 'file', 'index', 'gitignore', 'gitignore-add', 'context-exclude', or 'calibration'")]
        string action,
        [Description(
            "Content filter for 'describe': 'code', 'logs', 'files', 'conversation', 'toolcalls', or a file path substring")]
        string? filter = null,
        [Description(
            "For 'block': sha256 prefix from a code_block placeholder. For 'file': workspace-relative path. For 'index': folder path (omit for root).")]
        string? id = null,
        [Description("Optional session ID for health tracking (used with 'block')")]
        string? sessionId = null,
        [Description("Set to true to confirm write for 'gitignore-add' or 'context-exclude'")]
        bool confirmed = false,
        [Description("Workspace root override; defaults to the configured workspace root")]
        string? root = null,
        [Description("Paths to exclude for 'context-exclude' action (workspace-relative directories)")]
        string[]? paths = null,
        McpServer server = null!,
        CancellationToken ct = default)
    {
        await workspaceRootResolver.EnsureResolvedAsync(server, ct);

        return action.Trim().ToUpperInvariant() switch
        {
            "DESCRIBE" => DoDescribe(filter),
            "BLOCK" => DoBlock(id, sessionId),
            "STATS" => DoStats(),
            "FILE" => await DoGetFileAsync(id, root),
            "INDEX" => await DoGetIndexAsync(id, root),
            "GITIGNORE" => DoGitignoreCheck(root),
            "GITIGNORE-ADD" => DoGitignoreAdd(confirmed, root),
            "CONTEXT-EXCLUDE" => DoContextExclude(confirmed, paths, root),
            "CALIBRATION" => DoCalibration(root),
            "SELF-TEST" when cleaner.IsDebugEnabled => await DoSelfTestAsync(ct),
            _ =>
                "Unknown action. Use 'describe', 'block', 'stats', 'file', 'index', 'gitignore', 'gitignore-add', 'context-exclude', or 'calibration'."
        };
    }

    // ── Describe ──────────────────────────────────────────────────────────────

    private string DoDescribe(string? filter)
    {
        PipelineSummary? summary = cleaner.LastSummary;
        if (summary == null)
        {
            return "No context has been built yet in this session. Call BuildConciseContext first.";
        }

        StringBuilder sb = new();
        _ = sb.AppendLine(summary.NarrativeSummary);
        _ = sb.AppendLine();

        List<ManifestEntry> entries = summary.Manifest.Filter(filter).ToList();
        if (!(entries.Count > 0))
        {
            _ = sb.AppendLine(filter != null
                ? $"No context entries match filter '{filter}'."
                : "Context manifest is empty.");
            return sb.ToString().TrimEnd();
        }

        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Matching entries ({entries.Count}):");
        foreach (ManifestEntry? e in entries)
        {
            string status = e.WasDropped ? "DROPPED" :
                e.WasCollapsed ? "collapsed" :
                e.WasSummarized ? "summarized" : "verbatim";
            string relevance = e.Relevance < 1.0 ? $" rel={e.Relevance:F2}" : "";
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  [{e.Kind,-12}] {e.Source}#{e.SectionName}  {e.TokenCount}tok  {status}{relevance}");
            if (e.WasDropped && e.DroppedReason != null)
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"               ↳ {e.DroppedReason}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    // ── Block ─────────────────────────────────────────────────────────────────

    private string DoBlock(string? id, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return "id is required for action 'block'.";
        }

        string key = id.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? id[7..] : id;

        PipelineSummary? summary = cleaner.LastSummary;
        if (summary == null)
        {
            return "No context available. Build context first.";
        }

        if (sessionId != null)
        {
            SessionSignals signals = cleaner.GetOrCreateSignals(sessionId);
            StrippingHealthAnalyzer.RecordCodeBlockRequest(signals);
        }

        ManifestEntry? entry = summary.Manifest.Entries.FirstOrDefault(e =>
            e.ChunkId.Equals(key, StringComparison.OrdinalIgnoreCase) ||
            e.SectionName.Contains(key, StringComparison.OrdinalIgnoreCase));

        return entry == null
            ? $"Code block '{id}' not found in context. It may have been evicted or the id is incorrect."
            : $"Code block found in: {entry.Source} § {entry.SectionName}\n" +
               $"Status: {(entry.WasCollapsed ? "was collapsed" : "not collapsed")} | Tokens: {entry.TokenCount}\n\n" +
               "(Full content was collapsed during context build. " +
               "Request the file directly via BuildConciseContext to restore it.)";
    }

    // ── Stats ─────────────────────────────────────────────────────────────────

    private string DoStats()
    {
        PipelineSummary? summary = cleaner.LastSummary;
        if (summary == null)
        {
            return "No pipeline stats available. Build context first.";
        }

        StringBuilder sb = new();
        _ = sb.AppendLine(CultureInfo.InvariantCulture,
            $"Pipeline Stats  |  Total: {summary.TotalInputTokens:N0} → {summary.TotalOutputTokens:N0} tokens  " +
            $"saved {summary.TotalSavingsPercent}%  in {summary.TotalElapsed.TotalMilliseconds:N0}ms");
        _ = sb.AppendLine();
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"{"Stage",-30} {"In",8} {"Out",8} {"Saved%",8} {"Dropped",8} {"Collapsed",10} {"ms",6}");
        _ = sb.AppendLine(new string('-', 82));

        foreach (StageDiagnostic stage in summary.Stages)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture,
                $"{stage.StageName,-30} {stage.InputTokens,8:N0} {stage.OutputTokens,8:N0} " +
                $"{stage.SavingsPercent,7:F1}% {stage.ItemsDropped,8} {stage.ItemsCollapsed,10} " +
                $"{stage.Elapsed.TotalMilliseconds,6:N0}");
            foreach (string note in stage.Notes)
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  ↳ {note}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    // ── Calibration ───────────────────────────────────────────────────────────

    private string DoCalibration(string? root)
    {
        string workspaceRoot = root ?? workspaceConfig.GetWorkspaceRoot();
        RetrievalCalibrationState state = retrievalCalibration.GetStats(workspaceRoot);

        StringBuilder sb = new();
        _ = sb.AppendLine(CultureInfo.InvariantCulture,
            $"Retrieval calibration  |  threshold adjustment: {state.ThresholdAdjustment:+0.00;-0.00;+0.00} " +
            $"(baseline SemanticThreshold unchanged; effective = baseline + this)");
        _ = sb.AppendLine(CultureInfo.InvariantCulture,
            $"  Samples: {state.TotalSamples} total, {state.TotalFailures} narrow-filter failures " +
            $"({(state.TotalSamples == 0 ? 0 : (double)state.TotalFailures / state.TotalSamples):P0})");
        _ = sb.AppendLine(CultureInfo.InvariantCulture,
            $"  Consecutive: {state.ConsecutiveFailures} failures, {state.ConsecutivePasses} clean turns");
        if (!string.IsNullOrEmpty(state.LastUpdated))
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  Last updated: {state.LastUpdated}");
        }

        if (state.ThresholdAdjustment == 0 && state.TotalSamples == 0)
        {
            _ = sb.AppendLine();
            _ = sb.AppendLine(
                "No calibration samples yet — this accumulates when a session requests a file in a " +
                "later BuildConciseContext turn that the semantic relevance filter didn't surface " +
                "above its threshold on an earlier turn.");
        }

        return sb.ToString().TrimEnd();
    }

    // ── Self-test ─────────────────────────────────────────────────────────────

    private async Task<string> DoSelfTestAsync(CancellationToken ct)
    {
        ContextSelfTestResult result = await cleaner.RunSelfTestAsync(ct);

        string verdict = result.Passed
            ? string.Format(CultureInfo.InvariantCulture, "✓ PASS — synthetic payload shrank {0:N0} → {1:N0} tokens (saved {2}%).", result.RawTokens, result.CleanedTokens, result.SavingsPercent)
            : string.Format(CultureInfo.InvariantCulture, "✗ FAIL — synthetic payload did NOT shrink: {0:N0} → {1:N0} tokens. The cleaning pipeline appears broken (a stage may have regressed to a no-op).", result.RawTokens, result.CleanedTokens);

        StringBuilder sb = new();
        _ = sb.AppendLine(verdict);
        _ = sb.AppendLine();
        _ = sb.AppendLine(result.Narrative);

        return sb.ToString().TrimEnd();
    }

    // ── File / Index retrieval ────────────────────────────────────────────────

    private async Task<string> DoGetFileAsync(string? id, string? root)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return "id is required for action 'file'. Provide a workspace-relative path, e.g. 'docs/guide.md'.";
        }

        string? content = await contextPipeline.GetFileContentAsync(id, root);
        return content ?? $"'{id}' has not been indexed yet. Call BuildConciseContext with that file first.";
    }

    private async Task<string> DoGetIndexAsync(string? id, string? root)
    {
        string? json = await folderTree.GetFolderIndexJsonAsync(id, root);
        if (json == null)
        {
            string path = string.IsNullOrWhiteSpace(id) ? "root" : $"'{id}'";
            return $"No index found for {path}. Call BuildConciseContext first to populate the index.";
        }

        return json;
    }

    // ── Gitignore check ───────────────────────────────────────────────────────

    private string DoGitignoreCheck(string? root)
    {
        string workspaceRoot = root ?? workspaceConfig.GetWorkspaceRoot();
        IReadOnlyList<GitignoreIssue> known = GitignoreScanner.Scan(workspaceRoot);
        IReadOnlyList<SuspiciousDirectory> suspicious = SuspiciousDirectoryScanner.Scan(workspaceRoot);

        if (known.Count == 0 && suspicious.Count == 0)
        {
            return
                $"✓ No gitignore issues found — known patterns and heuristic scan both clean.\n(workspace: {workspaceRoot})";
        }

        StringBuilder sb = new();
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Workspace: {workspaceRoot}");
        _ = sb.AppendLine();

        // ── Section 1: known patterns ──────────────────────────────────────────
        if (known.Count > 0)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"⚠ CONFIRMED — {known.Count} known generated/cache path(s) not in .gitignore:");
            foreach (GitignoreIssue issue in known)
            {
                string secretsTag = issue.ContainsSecrets ? "  ⚠ MAY CONTAIN SECRETS" : "";
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  {issue.Entry}{(issue.IsFile ? "" : "/")}{secretsTag}");
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"    {issue.Reason}");
            }

            if (known.Any(i => i.ContainsSecrets))
            {
                _ = sb.AppendLine(
                    "  ⚠ Entries marked MAY CONTAIN SECRETS could expose API keys, file contents, or credentials.");
            }

            _ = sb.AppendLine();
        }

        // ── Section 2: suspicious (heuristic) ─────────────────────────────────
        if (suspicious.Count > 0)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"🔍 SUSPICIOUS — {suspicious.Count} heuristically detected path(s), not covered by .gitignore:");
            _ = sb.AppendLine(
                "   (Review before acting — these are pattern matches and content heuristics, not definitive)");
            _ = sb.AppendLine();
            foreach (SuspiciousDirectory? s in suspicious.OrderByDescending(x => x.Confidence))
            {
                string confPct = $"{s.Confidence:P0}";
                string recTag = s.Recommendation switch
                {
                    SuspiciousDirRecommendation.AddToGitignore => "→ gitignore",
                    SuspiciousDirRecommendation.ExcludeFromContext => "→ context-exclude",
                    _ => "→ both"
                };
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  [{confPct,4} conf] {s.RelativePath}/  {recTag}");
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"    {s.Reason}");
            }

            _ = sb.AppendLine();
        }

        // ── Actions ────────────────────────────────────────────────────────────
        _ = sb.AppendLine("Actions:");

        bool hasGitignoreItems = known.Count > 0 ||
                                suspicious.Any(s => s.Recommendation is
                                    SuspiciousDirRecommendation.AddToGitignore or SuspiciousDirRecommendation.Both);

        bool hasContextItems = suspicious.Any(s => s.Recommendation is
            SuspiciousDirRecommendation.ExcludeFromContext or SuspiciousDirRecommendation.Both);

        if (hasGitignoreItems)
        {
            _ = sb.AppendLine(
                "  ContextDiag(action='gitignore-add', confirmed=true)  — add all gitignore-recommended entries to .gitignore");
        }

        if (hasContextItems)
        {
            IEnumerable<string> paths = suspicious
                .Where(s => s.Recommendation is
                    SuspiciousDirRecommendation.ExcludeFromContext or SuspiciousDirRecommendation.Both)
                .Select(s => $"\"{s.RelativePath}\"");
            _ = sb.AppendLine(CultureInfo.InvariantCulture,
                $"  ContextDiag(action='context-exclude', confirmed=true, paths=[{string.Join(", ", paths)}])");
            _ = sb.AppendLine(
                "  — append to .tokensaver/.ignore-additional (keeps them in git but hides from AI context)");
        }

        return sb.ToString().TrimEnd();
    }

    // ── Gitignore add ─────────────────────────────────────────────────────────

    private string DoGitignoreAdd(bool confirmed, string? root)
    {
        if (!confirmed)
        {
            return
                "Set confirmed=true to write to .gitignore. Run ContextDiag(action='gitignore') first to review what will be added.";
        }

        string workspaceRoot = root ?? workspaceConfig.GetWorkspaceRoot();
        IReadOnlyList<GitignoreIssue> known = GitignoreScanner.Scan(workspaceRoot);
        List<SuspiciousDirectory> suspicious = SuspiciousDirectoryScanner.Scan(workspaceRoot)
            .Where(s => s.Recommendation is
                SuspiciousDirRecommendation.AddToGitignore or SuspiciousDirRecommendation.Both)
            .ToList();

        if (known.Count == 0 && suspicious.Count == 0)
        {
            return "Nothing to add — all gitignore-recommended entries are already covered.";
        }

        try
        {
            IEnumerable<(string, bool, string Reason)> allEntries = known
                .Select(i => (i.Entry, i.IsFile, i.Reason))
                .Concat(suspicious.Select(s => (s.RelativePath, false, s.Reason)));

            GitignoreFilter.AddEntries(workspaceRoot, allEntries);

            StringBuilder sb = new();
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"✓ Added {known.Count + suspicious.Count} entries to .gitignore:");
            foreach (GitignoreIssue issue in known)
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  {issue.Entry}{(issue.IsFile ? "" : "/")}  (confirmed)");
            }

            foreach (SuspiciousDirectory? s in suspicious)
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  {s.RelativePath}/  ({s.Confidence:P0} confidence)");
            }

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"Failed to update .gitignore: {ex.Message}";
        }
    }

    // ── Context exclude ───────────────────────────────────────────────────────

    private string DoContextExclude(bool confirmed, string[]? paths, string? root)
    {
        if (!confirmed)
        {
            return "Set confirmed=true to save context exclusions. " +
                   "Run ContextDiag(action='gitignore') first to identify paths, then call with " +
                   "confirmed=true and paths=[...] listing workspace-relative directories to exclude.";
        }

        if (paths is null || paths.Length == 0)
        {
            return "paths[] is required for context-exclude. " +
                   "Provide workspace-relative directory paths to exclude from context building and code indexing.";
        }

        string workspaceRoot = root ?? workspaceConfig.GetWorkspaceRoot();

        try
        {
            // Directory paths are turned into gitignore-syntax directory patterns (trailing "/")
            // and appended to .tokensaver/.ignore-additional — the same file/format ManageConfig's
            // "ignore-additional" action writes to, so there's a single gitignore-style mechanism
            // for "exclude from indexing/context but not from git" instead of two.
            string[] patterns = paths
                .Select(p => p.Replace('\\', '/').Trim('/') + "/")
                .ToArray();
            string filePath = WorkspaceIgnoreFilter.AdditionalPath(workspaceRoot);
            IReadOnlyList<string> added = WorkspaceIgnoreFilter.Append(filePath, patterns);

            StringBuilder sb = new();
            if (added.Count == 0)
            {
                _ = sb.AppendLine("Nothing added — all supplied paths were already present in .ignore-additional.");
            }
            else
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"✓ Appended {added.Count} pattern(s) to .tokensaver/.ignore-additional:");
                foreach (string p in added)
                {
                    _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  {p}");
                }
            }

            _ = sb.AppendLine();
            _ = sb.AppendLine("These paths will be skipped by BuildConciseContext and the code indexer.");
            _ = sb.AppendLine(
                "They remain tracked by git. To also exclude from git, run ContextDiag(action='gitignore-add', confirmed=true).");
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"Failed to save context exclusions: {ex.Message}";
        }
    }
}
