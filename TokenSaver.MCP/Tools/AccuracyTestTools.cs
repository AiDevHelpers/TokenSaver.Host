using AiDevTools.TokenSaver.CodeHelper.Indexing;
using AiDevTools.TokenSaver.TokenEconomy.Models.Accuracy;
using AiDevTools.TokenSaver.OllamaIntegration.Services; using AiDevTools.TokenSaver.Common.Models;
using AiDevTools.TokenSaver.MCP.Services;

using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AiDevTools.TokenSaver.TokenEconomy.Services;


namespace AiDevTools.TokenSaver.MCP.Tools;

/// <summary>
///     Workflow: AgentConfig(action: 'test') → AgentConfig(action: 'get') → AgentConfig(action: 'update')
///     Stage proposals first with AgentConfig(action: 'save'), then apply them with 'update'.
/// </summary>
[McpServerToolType]
internal sealed class AccuracyTestTools(
    WorkspaceCodeScanner scanner,
    AccuracyTestService tester,
    AgentConfigService agentConfig,
    WorkspaceConfigManager workspaceIndex,
    WorkspaceRootSessionResolver workspaceRootResolver)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    // ── AgentConfig ───────────────────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Manage SimpleModelServer agent configuration and accuracy testing. action determines what happens:\n" +
        "  test   — scan the workspace, run accuracy tests against the local model, return a scored report. " +
        "Always ask the user the setup questions first, then re-call with confirmed=true.\n" +
        "  get    — return current agent system prompts and sampling parameters for review.\n" +
        "  update — apply changes to an agent's system prompt or sampling parameters. agentType required; " +
        "at least one of systemPrompt, temperature, topP must be provided.\n" +
        "  save   — stage proposed prompt changes to .tokensaver/agent-prompts.json for review before applying. " +
        "proposalsJson required: JSON object keyed by agent type → {systemPrompt, temperature, topP}.")]
    public async Task<string> AgentConfig(
        [Description("Action: 'test', 'get', 'update', or 'save'")]
        string action,
        // test params
        [Description("Must be true for 'test' — set after presenting setup questions to the user")]
        bool confirmed = false,
        [Description("Agent type to test: code, debug, or planning (for 'test')")]
        string agentType = "code",
        [Description("Number of tests to run (for 'test', default 10)")]
        int maxTests = 10,
        [Description("Workspace root path override (for 'test')")]
        string? workspaceRoot = null,
        // update params
        [Description("Agent type to update: code, debug, or planning (required for 'update')")]
        string? updateAgentType = null,
        [Description("New system prompt (for 'update')")]
        string? systemPrompt = null,
        [Description("New temperature 0.0–1.0 (for 'update')")]
        double? temperature = null,
        [Description("New top_p 0.0–1.0 (for 'update')")]
        double? topP = null,
        // save params
        [Description("JSON object mapping agent type → {systemPrompt, temperature, topP} (required for 'save')")]
        string? proposalsJson = null,
        McpServer server = null!,
        CancellationToken ct = default)
    {
        await workspaceRootResolver.EnsureResolvedAsync(server, ct);

        return action.Trim().ToUpperInvariant() switch
        {
            "TEST" => await DoTest(confirmed, agentType, maxTests, workspaceRoot, ct),
            "GET" => await DoGet(ct),
            "UPDATE" => await DoUpdate(updateAgentType, systemPrompt, temperature, topP, ct),
            "SAVE" => await DoSave(proposalsJson, ct),
            _ => "Unknown action. Use 'test', 'get', 'update', or 'save'."
        };
    }

    private async Task<string> DoTest(
        bool confirmed, string agentType, int maxTests, string? workspaceRoot, CancellationToken ct)
    {
        if (!confirmed)
        {
            string root = workspaceRoot ?? workspaceIndex.GetWorkspaceRoot();
            return "Before running the TokenSaver Tests, ask the user these setup questions:\n\n" +
                   $"1. Which agent type should be tested? (current default: {agentType} — options: code, debug, planning)\n" +
                   $"2. How many tests should be run? (current default: {maxTests})\n" +
                   $"3. Should the workspace root be overridden? (current: {root})\n\n" +
                   "Once the user has answered, re-call AgentConfig(action: 'test', confirmed: true) with the chosen values.";
        }

        string root2 = workspaceRoot ?? workspaceIndex.GetWorkspaceRoot();
        List<TestCase> cases = await scanner.ScanAsync(root2, maxTests * 3, ct);

        cases = [.. cases
            .Where(c => string.Equals(c.AgentType, agentType, StringComparison.OrdinalIgnoreCase))
            .Take(maxTests)];

        if (cases.Count == 0)
        {
            return "No test cases could be generated. " +
                   "Ensure the workspace contains .cs or .ts source files with method definitions.";
        }

        TestReport report = await tester.RunAllAsync(cases, root2, ct);
        return FormatReport(report);
    }

    private async Task<string> DoGet(CancellationToken ct)
    {
        string? raw = await agentConfig.GetRawConfigAsync(ct);
        if (raw is null)
        {
            return "SimpleModelServer admin API unavailable. " +
                   "Check that the router is running on OllamaUrl (default http://localhost:11435) and that GET /admin/config is reachable from localhost.";
        }

        try
        {
            string pretty = JsonSerializer.Serialize(
                JsonSerializer.Deserialize<JsonElement>(raw), JsonOpts);
            return $"```json\n{pretty}\n```";
        }
        catch
        {
            return $"```json\n{raw}\n```";
        }
    }

    private async Task<string> DoUpdate(
        string? agentType, string? systemPrompt, double? temperature, double? topP, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(agentType))
        {
            return "updateAgentType is required for action 'update'.";
        }

        if (systemPrompt is null && temperature is null && topP is null)
        {
            return "Provide at least one of systemPrompt, temperature, or topP.";
        }

        (bool success, string? message) = await agentConfig.UpdateAgentAsync(agentType, systemPrompt, temperature, topP, ct);

        if (!success)
        {
            return $"Update failed: {message}";
        }

        StringBuilder sb = new();
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Updated '{agentType}' agent config.");
        _ = sb.AppendLine(message);
        if (systemPrompt is not null)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  systemPrompt: {systemPrompt.Length} chars");
        }

        if (temperature is not null)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  temperature:  {temperature}");
        }

        if (topP is not null)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  top_p:        {topP}");
        }

        _ = sb.AppendLine();
        _ = sb.AppendLine(
            "Changes are live for new SimpleModelServer requests. Re-run AgentConfig(action: 'test') to measure the impact.");
        return sb.ToString().TrimEnd();
    }

    private async Task<string> DoSave(string? proposalsJson, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(proposalsJson))
        {
            return "proposalsJson is required for action 'save'.";
        }

        Dictionary<string, AgentConfigSnapshot> proposals;
        try
        {
            proposals = JsonSerializer.Deserialize<Dictionary<string, AgentConfigSnapshot>>(
                ExtractJsonBlock(proposalsJson), JsonOpts) ?? [];
        }
        catch (Exception ex)
        {
            return $"Failed to parse proposalsJson: {ex.Message}";
        }

        if (proposals.Count == 0)
        {
            return "No proposals found in the provided JSON.";
        }

        (bool ok, string? path) = await agentConfig.SaveProposedPromptsAsync(proposals, ct);
        return ok
            ? $"Saved {proposals.Count} proposed prompt(s) to {path}.\n" +
              "Review the file, then call AgentConfig(action: 'update') to apply individual changes."
            : "Failed to save proposals — ensure SIMPLEMODELSERVER_WORKSPACE is set.";
    }

    // ── Formatting helpers ────────────────────────────────────────────────────

    private static string FormatReport(TestReport report)
    {
        StringBuilder sb = new();
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"## Accuracy Report — {report.AgentType} agent");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Workspace: {report.WorkspaceRoot}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Generated: {report.GeneratedAt:u}");
        _ = sb.AppendLine();
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"**Pass rate: {report.PassedTests}/{report.TotalTests} ({report.PassRate:P0})**  " +
                      $"avg score: {report.AverageScore:F2}  avg latency: {report.AverageLatency.TotalMilliseconds:F0}ms");
        _ = sb.AppendLine();

        if (report.ByCategory.Count > 0)
        {
            _ = sb.AppendLine("### By category");
            foreach ((string? cat, CategoryStats? stats) in report.ByCategory)
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"- **{cat}**: {stats.Passed}/{stats.Total} passed, avg score {stats.AvgScore:F2}");
            }

            _ = sb.AppendLine();
        }

        if (report.CommonFailures.Count > 0)
        {
            _ = sb.AppendLine("### Most common missing patterns");
            foreach ((string? pattern, int count) in report.CommonFailures.OrderByDescending(x => x.Value).Take(5))
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"- `{pattern}` — missed {count}x");
            }

            _ = sb.AppendLine();
        }

        _ = sb.AppendLine("### Test results");
        foreach (TestResult r in report.Results)
        {
            string icon = r.Passed ? "✓" : "✗";
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"{icon} [{r.TestCase.Category}] {r.TestCase.SourceFile} — score {r.Score:F2} ({(int)r.Latency.TotalMilliseconds}ms)");
            if (!r.Passed && r.Notes is not null)
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  → {r.Notes}");
            }

            if (r.JudgeVerdict is not null)
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  → judge: {r.JudgeVerdict}");
            }
        }

        _ = sb.AppendLine();
        _ = sb.AppendLine("---");
        _ = sb.AppendLine("To improve: call AgentConfig(action: 'get') to review the current system prompt, " +
                      "then AgentConfig(action: 'update') with a revised prompt targeting failing categories.");

        return sb.ToString().TrimEnd();
    }

    private static string ExtractJsonBlock(string input)
    {
        int start = input.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
        if (start >= 0)
        {
            start = input.IndexOf('\n', start) + 1;
            int end = input.IndexOf("```", start, StringComparison.Ordinal);
            if (end > start)
            {
                return input[start..end].Trim();
            }
        }

        return input.Trim();
    }
}
