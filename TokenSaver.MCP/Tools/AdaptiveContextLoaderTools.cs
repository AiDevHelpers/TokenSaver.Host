using TokenSaver.CodeHelper.ContextSegmentation;
using TokenSaver.CodeHelper.Indexing;
using TokenSaver.CodeHelper.Planning;
using TokenSaver.Common.Core.Configuration;
using TokenSaver.MCP.Services;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Text;

using CultureInfo = System.Globalization.CultureInfo;

namespace TokenSaver.MCP.Tools;

/// <summary>
/// Intelligent context loading that splits codebase into task-relevant segments.
/// Learns from access patterns to optimize for token efficiency while preserving task-critical content.
/// </summary>
[McpServerToolType]
internal sealed class AdaptiveContextLoaderTools
{
    private readonly AdaptiveContextLoader _loader;
    private readonly ContextSegmentationService _segmentation;
    private readonly AccessPatternTracker _tracker;
    private readonly TokenSaverConfig _config;
    private readonly WorkspaceConfigManager _workspaceConfig;
    private readonly WorkspaceRootSessionResolver _workspaceRootResolver;
    private readonly ILogger<AdaptiveContextLoaderTools> _logger;

    public AdaptiveContextLoaderTools(
        AdaptiveContextLoader loader,
        ContextSegmentationService segmentation,
        AccessPatternTracker tracker,
        IOptions<TokenSaverConfig> config,
        WorkspaceConfigManager workspaceConfig,
        WorkspaceRootSessionResolver workspaceRootResolver,
        ILogger<AdaptiveContextLoaderTools> logger)
    {
        _loader = loader;
        _segmentation = segmentation;
        _tracker = tracker;
        _config = config.Value;
        _workspaceConfig = workspaceConfig;
        _workspaceRootResolver = workspaceRootResolver;
        _logger = logger;
    }

    // ── AnalyzeContextSegmentation ────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Analyze the current codebase segmentation and access patterns. " +
        "action determines what to show:\n" +
        "  segments   — list all defined segments with priorities and token estimates.\n" +
        "  patterns   — show access pattern statistics and segment co-occurrence affinities.\n" +
        "  recommend  — analyze a prompt and recommend which segments to load.")]
    public async Task<string> AnalyzeContextSegmentation(
        [Description("Action: 'segments', 'patterns', or 'recommend'")]
        string action,
        [Description("User prompt — required for 'recommend' action")]
        string? prompt = null,
        [Description("Token budget for segment selection (default 50000)")]
        int tokenBudget = 50000,
        McpServer server = null!,
        CancellationToken ct = default)
    {
        await _workspaceRootResolver.EnsureResolvedAsync(server, ct);

        return action.Trim().ToUpperInvariant() switch
        {
            "SEGMENTS" => await DoSegments(ct),
            "PATTERNS" => await DoPatterns(ct),
            "RECOMMEND" => await DoRecommend(prompt, tokenBudget, ct),
            _ => "Unknown action. Use 'segments', 'patterns', or 'recommend'."
        };
    }

    private async Task<string> DoSegments(CancellationToken ct)
    {
        var segments = await _segmentation.GetSegmentsAsync(ct);
        var sb = new StringBuilder();
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Defined Segments ({segments.Count} total):\n");

        var byPriority = segments.GroupBy(s => s.Priority).OrderBy(g => g.Key);
        foreach (var group in byPriority)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"[{group.Key}]");
            foreach (var seg in group.OrderBy(s => s.Label))
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  {seg.Label,-30} ({seg.Id,-25}) ~{seg.EstimatedTokens:N0} tokens");
                if (seg.PrimaryTaskTypes.Length > 0)
                {
                    _ = sb.AppendLine(CultureInfo.InvariantCulture,
                        $"    Primary tasks: {string.Join(", ", seg.PrimaryTaskTypes)}");
                }

                _ = sb.AppendLine(CultureInfo.InvariantCulture,
                    $"    Patterns: {string.Join(", ", seg.PathPatterns.Take(3))}");
                if (seg.LastAccessed != DateTime.MinValue)
                {
                    _ = sb.AppendLine(CultureInfo.InvariantCulture,
                        $"    Last used: {seg.LastAccessed:F}");
                }
            }

            _ = sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private async Task<string> DoPatterns(CancellationToken ct)
    {
        var affinities = await _tracker.AnalyzeAffinitiesAsync(ct);

        if (affinities.Count == 0)
        {
            return "No access patterns recorded yet. Access patterns are learned as you work with the codebase.";
        }

        var sb = new StringBuilder();
        _ = sb.AppendLine("Segment Co-occurrence Affinities (recently learned):\n");

        var sorted = affinities.Values
            .OrderByDescending(a => a.Affinity)
            .ThenByDescending(a => a.CooccurrenceCount)
            .Take(20);

        foreach (var affinity in sorted)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture,
                $"  {affinity.Segment1,-25} ↔ {affinity.Segment2,-25} " +
                $"(affinity: {affinity.Affinity:F2}, co-occurrences: {affinity.CooccurrenceCount})");
        }

        if (affinities.Count > 20)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"\n  ... and {affinities.Count - 20} more affinities");
        }

        return sb.ToString().TrimEnd();
    }

    private async Task<string> DoRecommend(string? prompt, int tokenBudget, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return "prompt is required for action 'recommend'.";
        }

        var plan = await _loader.PlanContextLoadAsync(prompt, tokenBudget, ct);

        var sb = new StringBuilder();
        _ = sb.AppendLine(CultureInfo.InvariantCulture,
            $"Context Load Plan (Session: {plan.SessionId})\n");
        _ = sb.AppendLine(CultureInfo.InvariantCulture,
            $"Detected Task Type: {plan.DetectedTaskType} (confidence: {plan.TaskTypeConfidence:F2})");
        _ = sb.AppendLine(CultureInfo.InvariantCulture,
            $"Total Token Budget: {tokenBudget:N0} tokens");
        _ = sb.AppendLine(CultureInfo.InvariantCulture,
            $"Segments to Load: {plan.SegmentsToLoad.Count} ({plan.TotalEstimatedTokens:N0} tokens)\n");

        var byPriority = plan.SegmentsToLoad.GroupBy(s => s.Priority).OrderBy(g => g.Key);
        foreach (var group in byPriority)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"[{group.Key}]");
            foreach (var seg in group.OrderBy(s => s.Label))
            {
                string marker = plan.RecommendedSegmentIds.Contains(seg.Id) ? "✓ (recommended)" : "";
                _ = sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  {seg.Label,-30} (~{seg.EstimatedTokens:N0} tokens) {marker}");
            }
        }

        int remainingTokens = tokenBudget - plan.TotalEstimatedTokens;
        _ = sb.AppendLine(CultureInfo.InvariantCulture,
            $"\nRemaining Budget: {remainingTokens:N0} tokens");

        return sb.ToString().TrimEnd();
    }

    // ── BuildAdaptiveContext ──────────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Load context segments based on detected task type and learned access patterns. " +
        "Returns selected segments in workspace-relative format, ready to load via individual file reads. " +
        "If sessionId is provided, records this session for future pattern learning.")]
    public async Task<string> BuildAdaptiveContext(
        [Description("User prompt to analyze")]
        string prompt,
        [Description("Optional session identifier for pattern tracking")]
        string? sessionId = null,
        [Description("Token budget for segment selection (default 50000)")]
        int tokenBudget = 50000,
        [Description("Specific segment IDs to force-include (overrides task-type filtering)")]
        string[]? forceIncludeSegments = null,
        [Description("Workspace root path")]
        string? workspaceRoot = null,
        McpServer server = null!,
        CancellationToken ct = default)
    {
        await _workspaceRootResolver.EnsureResolvedAsync(server, ct);

        var plan = await _loader.PlanContextLoadAsync(prompt, tokenBudget, ct);

        // Force-include specific segments if requested
        if (forceIncludeSegments?.Length > 0)
        {
            var allSegments = await _segmentation.GetSegmentsAsync(ct);
            var forced = allSegments.Where(s => forceIncludeSegments.Contains(s.Id)).ToList();
            plan.SegmentsToLoad.AddRange(forced.Where(f => !plan.SegmentsToLoad.Any(s => s.Id == f.Id)));
        }

        var sb = new StringBuilder();
        _ = sb.AppendLine("Adaptive Context Load Plan");
        _ = sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "Task Type: {0} | Segments: {1} | Tokens: {2:N0}/{3:N0}",
            plan.DetectedTaskType,
            plan.SegmentsToLoad.Count,
            plan.TotalEstimatedTokens,
            tokenBudget));

        foreach (var segment in plan.SegmentsToLoad.OrderBy(s => s.Priority).ThenBy(s => s.Label))
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"[{segment.Priority,-10}] {segment.Label}");
            foreach (var pattern in segment.PathPatterns)
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  {pattern}");
            }
        }

        _ = sb.AppendLine();
        _ = sb.AppendLine("To load these segments, use this list of path patterns with your file retrieval tool.");
        _ = sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "Session ID for tracking: {0}",
            plan.SessionId));

        return sb.ToString().TrimEnd();
    }

    // ── RecordContextSession ──────────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Record how segments were actually used in a session for pattern learning. " +
        "Call this when a session ends to train the adaptive loader.")]
    public async Task<string> RecordContextSession(
        [Description("Session ID from BuildAdaptiveContext or AnalyzeContextSegmentation")]
        string sessionId,
        [Description("Segment IDs that were actually loaded and used")]
        string[]? segmentsUsed = null,
        [Description("Segment IDs where files were modified")]
        string[]? segmentsModified = null,
        [Description("Session outcome: 'Succeeded', 'Failed', 'Abandoned'")]
        string outcome = "Succeeded",
        McpServer server = null!,
        CancellationToken ct = default)
    {
        await _workspaceRootResolver.EnsureResolvedAsync(server, ct);

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return "sessionId is required.";
        }

        if (!Enum.TryParse<SessionOutcome>(outcome, true, out SessionOutcome parsedOutcome))
        {
            return $"Unknown outcome '{outcome}'. Use 'Succeeded', 'Failed', or 'Abandoned'.";
        }

        var plan = new AdaptiveLoadPlan { SessionId = sessionId };
        await _loader.RecordSessionAsync(
            plan,
            segmentsUsed ?? [],
            segmentsModified ?? [],
            parsedOutcome,
            ct);

        return string.Format(CultureInfo.InvariantCulture,
            "Session {0} recorded: {1}\nUsed: {2} segments | Modified: {3} segments\n" +
            "This data will improve future segment recommendations.",
            sessionId,
            parsedOutcome,
            segmentsUsed?.Length ?? 0,
            segmentsModified?.Length ?? 0);
    }
}
