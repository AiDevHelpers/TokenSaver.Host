using AiDevHelpers.AgentHelpers.Core.Contracts;
using AiDevHelpers.AgentHelpers.Core.Models;
using AiDevHelpers.AgentHelpers.Core.Services;

using TokenSaver.CodeHelper.AutoWorkflow;
using TokenSaver.CodeHelper.Calibration;
using TokenSaver.CodeHelper.Configuration;
using TokenSaver.CodeHelper.Models.Calibration;
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
internal sealed class AutoWorkflowTools(
    CodeWorkflowService workflow,
    CalibrationService calibration,
    WorkflowSessionStore sessions,
    IOptions<TokenSaverConfig> libConfig,
    IOptions<CodeHelperConfig> codeConfig,
    ContextCleanerService cleaner,
    ICallerDetectionService detection,
    AgentAdapterRegistry registry,
    IHttpContextAccessor httpContext,
    WorkspaceRootSessionResolver workspaceRootResolver)
{
    private readonly CodeHelperConfig _codeCfg = codeConfig.Value;
    private readonly TokenSaverConfig _libCfg = libConfig.Value;

    // ── CodeWork ──────────────────────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Run the collaborative auto-workflow for code-build requests. action determines what happens:\n" +
        "  start    — intercept a code-build prompt, build a plan, assess capability, and return a " +
        "skeleton design request to send to the calling AI. prompt required. " +
        "Returns AUTO_DISABLED if the workflow is off, or DEFERRED if the prompt is not a code-build request.\n" +
        "  continue — supply the AI skeleton design; CodeHelper implements what it can and returns " +
        "deferred steps with handoff prompts. sessionId and skeleton required. " +
        "After reviewing, call Calibration(action: 'feedback') to update weights.")]
    public async Task<string> CodeWork(
        [Description("Action: 'start' or 'continue'")]
        string action,
        [Description("Original code-build request prompt (required for 'start')")]
        string? prompt = null,
        [Description("Session ID from 'start' (required for 'continue'; optional for 'start')")]
        string? sessionId = null,
        [Description("AI skeleton design response (required for 'continue')")]
        string? skeleton = null,
        [Description("Workspace root override")]
        string? root = null,
        McpServer server = null!,
        CancellationToken ct = default)
    {
        await workspaceRootResolver.EnsureResolvedAsync(server, ct);

        string r = root ?? _libCfg.WorkspaceRoot;
        return action.Trim().ToUpperInvariant() switch
        {
            "START" => await DoStart(prompt, sessionId, r, ct),
            "CONTINUE" => await DoContinue(sessionId, skeleton, r, ct),
            _ => "Unknown action. Use 'start' or 'continue'."
        };
    }

    private async Task<string> DoStart(string? prompt, string? sessionId, string root, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return "prompt is required for action 'start'.";
        }

        PipelineResult cleaned = await cleaner.CleanContextAsync(prompt, sessionId: sessionId, ct: ct);
        prompt = cleaned.ContextText;

        // Resolve caller format for research context enrichment
        CallerInfo caller = httpContext.HttpContext is { } hc ? detection.Detect(hc) : new CallerInfo();
        (IAgentAdapter? modelAdapter, IAgentAdapter? clientAdapter) = registry.ResolveBoth(caller);
        ContextFormat agentFormat = modelAdapter.GetFormat(caller).WithClientOverrides(clientAdapter.GetFormat(caller));
        ResearchContextFormat researchFmt = new()
        {
            WillAskForMissingContext = agentFormat.WillAskForMissingContext,
            UseXmlSectionTags = agentFormat.UseXmlSectionTags,
            UseMarkdownHeaders = agentFormat.UseMarkdownHeaders,
            CharBudget = agentFormat.ContextTokenBudget * 4,
            PerFileCharLimit = agentFormat.PerFileTokenLimit * 4
        };

        if (!calibration.ResolveAutoEnabled(_codeCfg.AutoEnabled, string.IsNullOrWhiteSpace(root) ? null : root))
        {
            return "AUTO_DISABLED: CodeHelper auto-workflow is off. " +
                   "Enable it with Calibration(action: 'toggle', enabled: true) or via the tokenSaver.autoCodeHelper.enabled VS Code setting.";
        }

        if (!workflow.DetectCodeBuildIntent(prompt))
        {
            return "DEFERRED: Prompt does not look like a code-build request. Proceeding normally.";
        }

        string sid = sessionId ?? Guid.NewGuid().ToString("N")[..12];
        WorkflowSession session = await workflow.StartWorkflowAsync(prompt, sid, root, researchFmt, ct);

        StringBuilder sb = new();
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"**Session ID:** `{session.SessionId}`");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"**Capability:** {session.Assessment?.Level} (score {session.Assessment?.Score:F2})");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"**Plan ID:** `{session.PlanId}`");
        _ = sb.AppendLine();

        if (session.Assessment?.Level == CapabilityLevel.ShouldDefer)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"DEFERRED: {session.Assessment.Justification}");
            _ = sb.AppendLine("CodeHelper is stepping aside — please implement this task directly.");
            return sb.ToString().TrimEnd();
        }

        _ = sb.AppendLine("**SKELETON REQUEST** — please send the following to the calling AI:");
        _ = sb.AppendLine();
        _ = sb.AppendLine(session.SkeletonRequestPrompt);
        _ = sb.AppendLine();
        _ = sb.AppendLine(CultureInfo.InvariantCulture,
            $"Once you have the skeleton, call: `CodeWork(action: 'continue', sessionId: \"{session.SessionId}\", skeleton: <AI response>)`");

        return sb.ToString().TrimEnd();
    }

    private async Task<string> DoContinue(string? sessionId, string? skeleton, string root, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return "sessionId is required for action 'continue'.";
        }

        if (string.IsNullOrWhiteSpace(skeleton))
        {
            return "skeleton is required for action 'continue'.";
        }

        WorkflowResult result;
        try
        {
            result = await workflow.ContinueWorkflowAsync(sessionId, skeleton, root, ct);
        }
        catch (InvalidOperationException ex)
        {
            return $"❌ {ex.Message}";
        }

        StringBuilder sb = new();
        _ = sb.AppendLine(result.Summary);

        if (result.DeferredSteps.Count > 0)
        {
            _ = sb.AppendLine();
            _ = sb.AppendLine("---");
            _ = sb.AppendLine("**Deferred step handoff prompts:**");
            foreach (DeferredStep d in result.DeferredSteps)
            {
                _ = sb.AppendLine();
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"### Step {d.StepNumber}: {d.Description}");
                if (d.FilePath is not null)
                {
                    _ = sb.AppendLine(CultureInfo.InvariantCulture, $"*File:* `{d.FilePath}`");
                }

                _ = sb.AppendLine();
                _ = sb.AppendLine(d.AIHandoffPrompt);
            }
        }

        return sb.ToString().TrimEnd();
    }

    // ── Calibration ───────────────────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Manage CodeHelper auto-workflow calibration. action determines what happens:\n" +
        "  toggle   — enable or disable the auto-workflow intercept at runtime. enabled required.\n" +
        "  stats    — return current capability weights, recent pass rate, and per-task trends.\n" +
        "  feedback — after reviewing CodeHelper scaffold output, report how much the AI had to fix. " +
        "sessionId, aiFixCount, aiFixSeverity (Minor|Major|Critical), and correctionRatio (0.0–1.0) required. " +
        "Adjusts calibration weights so future assessments improve over time.")]
    public async Task<string> Calibration(
        [Description("Action: 'toggle', 'stats', or 'feedback'")]
        string action,
        [Description("Enable flag (required for 'toggle'): true to enable, false to disable")]
        bool? enabled = null,
        [Description("Persistence scope for 'toggle': 'workspace' (default, writes to the workspace calibration file) or 'global' (writes to the user-level file)")]
        string scope = "workspace",
        [Description("Session ID from CodeWork 'start' (required for 'feedback')")]
        string? sessionId = null,
        [Description("Number of items the AI had to correct (required for 'feedback')")]
        int? aiFixCount = null,
        [Description("Severity of worst fix for 'feedback': Minor, Major, or Critical")]
        string? aiFixSeverity = null,
        [Description("Fraction of total work that was corrections, 0.0–1.0 (required for 'feedback')")]
        double? correctionRatio = null,
        [Description("Optional free-text notes for the audit log (for 'feedback')")]
        string? notes = null,
        [Description("Workspace root override")]
        string? root = null,
        McpServer server = null!,
        CancellationToken ct = default)
    {
        await workspaceRootResolver.EnsureResolvedAsync(server, ct);

        string r = root ?? _libCfg.WorkspaceRoot;
        return action.Trim().ToUpperInvariant() switch
        {
            "TOGGLE" => DoToggle(enabled, scope, r),
            "STATS" => DoStats(r),
            "FEEDBACK" => DoFeedback(sessionId, aiFixCount, aiFixSeverity, correctionRatio, notes, r),
            _ => "Unknown action. Use 'toggle', 'stats', or 'feedback'."
        };
    }

    private string DoToggle(bool? enabled, string scope, string root)
    {
        if (enabled is null)
        {
            return "enabled is required for action 'toggle'.";
        }

        bool isGlobal = string.Equals(scope, "global", StringComparison.OrdinalIgnoreCase);
        if (!isGlobal && string.IsNullOrWhiteSpace(root))
        {
            return "❌ Cannot toggle with scope 'workspace': no workspace root available. " +
                   "Pass root explicitly or use scope: 'global'.";
        }

        try
        {
            calibration.SetAutoEnabled(enabled.Value, scope, isGlobal ? null : root);
        }
        catch (ArgumentException ex)
        {
            return $"❌ {ex.Message}";
        }

        // Keep the in-memory config in sync for the remainder of this process too.
        _codeCfg.AutoEnabled = enabled.Value;

        string state = enabled.Value ? "enabled" : "disabled";
        return $"CodeHelper auto-workflow {state} ({scope}, persisted).";
    }

    private string DoStats(string root)
    {
        CalibrationStats stats = calibration.GetStats(string.IsNullOrWhiteSpace(root) ? null : root);
        CalibrationWeights w = stats.CurrentWeights;

        StringBuilder sb = new();
        _ = sb.AppendLine("## CodeHelper Calibration Stats");
        _ = sb.AppendLine();
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"**Overall pass rate:** {stats.RecentPassRate:P0} (last {stats.TotalOutcomes} outcomes)");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"**Global confidence:** {w.GlobalConfidence:F2}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"**Last updated:** {(string.IsNullOrEmpty(w.LastUpdated) ? "never" : w.LastUpdated)}");
        _ = sb.AppendLine();
        _ = sb.AppendLine("### Weights by task kind");
        foreach ((string? k, double v) in w.ByTaskKind.OrderBy(x => x.Key))
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"- **{k}**: {v:F2}");
        }

        _ = sb.AppendLine();
        _ = sb.AppendLine("### Weights by language");
        foreach ((string? k, double v) in w.ByLanguage.OrderBy(x => x.Key))
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"- `{k}`: {v:F2}");
        }

        if (stats.RecentTrends.Count > 0)
        {
            _ = sb.AppendLine();
            _ = sb.AppendLine("### Recent trends");
            foreach (string t in stats.RecentTrends)
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"- {t}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private string DoFeedback(
        string? sessionId, int? aiFixCount, string? aiFixSeverity,
        double? correctionRatio, string? notes, string root)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return "sessionId is required for action 'feedback'.";
        }

        if (aiFixCount is null)
        {
            return "aiFixCount is required for action 'feedback'.";
        }

        if (string.IsNullOrWhiteSpace(aiFixSeverity))
        {
            return "aiFixSeverity is required for action 'feedback'.";
        }

        if (correctionRatio is null)
        {
            return "correctionRatio is required for action 'feedback'.";
        }

        WorkflowSession? session = sessions.Get(sessionId);
        if (session is null)
        {
            return $"❌ Session '{sessionId}' not found.";
        }

        if (!Enum.TryParse<FixSeverity>(aiFixSeverity, true, out FixSeverity severity))
        {
            return $"❌ Unknown severity '{aiFixSeverity}'. Use Minor, Major, or Critical.";
        }

        CalibrationOutcome outcome = new(
            sessionId,
            session.PlanId ?? "",
            session.TaskKind,
            session.Language,
            aiFixCount.Value,
            severity,
            Math.Clamp(correctionRatio.Value, 0.0, 1.0),
            notes,
            DateTime.UtcNow);

        calibration.RecordOutcome(outcome, string.IsNullOrWhiteSpace(root) ? null : root);

        CalibrationStats stats = calibration.GetStats(string.IsNullOrWhiteSpace(root) ? null : root);
        bool wasUseful = correctionRatio.Value < _codeCfg.WasteThreshold;
        string verdict = wasUseful
            ? "✓ Scaffold was useful — weights nudged up."
            : $"⚠ High correction ratio ({correctionRatio:P0}) — weights nudged down.";

        StringBuilder sb = new();
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Calibration recorded for session `{sessionId}`. {verdict}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Recent pass rate: {stats.RecentPassRate:P0} (last {stats.TotalOutcomes} outcomes).");

        if (stats.RecentTrends.Count > 0)
        {
            _ = sb.AppendLine();
            _ = sb.AppendLine("**Trends:**");
            foreach (string t in stats.RecentTrends)
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  - {t}");
            }
        }

        return sb.ToString().TrimEnd();
    }
}
