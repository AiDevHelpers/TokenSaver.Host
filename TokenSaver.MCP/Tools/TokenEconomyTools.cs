using TokenSaver.Common.Core.Configuration;
using TokenSaver.Common.Contracts;
using TokenSaver.TokenEconomy.Models.TokenEconomy;
using TokenSaver.OllamaIntegration.Services;
using TokenSaver.MCP.Services;
using TokenSaver.TokenEconomy.Contracts;
using TokenSaver.Utilities.Services;

using Microsoft.Extensions.Options;

using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Text.Json;

namespace TokenSaver.MCP.Tools;

/// <summary>
///     Measurement/routing tools for the token-economy design: TokenBudget (pure, no LLM),
///     ModelRoute (task_type → model routing table), TokenSavingsReport/WorthKeeping (query the
///     savings history recorded by DistillationService via ISavingsTracker). See
///     TokenSaver/docs/token-economy-tools-contract.md.
/// </summary>
[McpServerToolType]
internal sealed class TokenEconomyTools(
    IDistillationService distill,
    ISavingsTracker savings,
    ModelRouteResolver modelRoutes,
    OllamaService ollama,
    IOptions<TokenSaverConfig> config,
    WorkspaceRootSessionResolver workspaceRootResolver)
{
    private readonly TokenSaverConfig _config = config.Value;

    // ── TokenBudget ──────────────────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Estimate the token cost of a piece of content and whether it exceeds a budget threshold. " +
        "Pure calculation, no model call — use before deciding whether to distill/truncate something yourself.")]
    public string TokenBudget(
        [Description("Text to measure")] string content,
        [Description("Token threshold to compare against (default 8000)")]
        int threshold = 8000)
    {
        int tokens = distill.EstimateTokens(content);
        var result = new
        {
            tokens,
            threshold,
            over_budget = tokens > threshold,
            pct_of_threshold = threshold > 0 ? (int)Math.Round(100.0 * tokens / threshold) : 0
        };
        return JsonSerializer.Serialize(result);
    }

    // ── ModelRoute ───────────────────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Call a local Ollama model chosen by task_type (code | chat | summarize | embed | rerank) rather than " +
        "naming a model directly — centralizes model choice per TokenEconomyConfig.ModelRoutes. " +
        "For embeddings use task_type='embed' and expect a JSON float array response, not a chat completion.")]
    public async Task<string> ModelRoute(
        [Description("Routing key: 'code', 'chat', 'summarize', 'embed', or 'rerank'")]
        string taskType,
        [Description("Prompt (or text to embed, when task_type='embed')")]
        string prompt,
        [Description("Max output tokens for non-embed task types")]
        int maxTokens = 400,
        CancellationToken ct = default)
    {
        string model = modelRoutes.Resolve(taskType);
        try
        {
            if (string.Equals(taskType, "embed", StringComparison.OrdinalIgnoreCase))
            {
                float[] vector = await ollama.EmbedAsync(model, prompt, ct: ct);
                return JsonSerializer.Serialize(vector);
            }

            return await ollama.CallModelAsync(model, prompt, ct);
        }
        catch (HttpRequestException ex)
        {
            return $"Model call failed for route '{taskType}' (model '{model}'): {ex.Message}";
        }
    }

    // ── TokenSavingsReport ───────────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Ranked rollup of recorded token savings, grouped by tool or role, over the last N days. " +
        "Sorted by saved_tokens descending, each row includes a worth_it verdict — the top row answers " +
        "\"what's saving the most tokens\" without manual aggregation.")]
    public async Task<string> TokenSavingsReport(
        [Description("Lookback window in days (default 7)")] int sinceDays = 7,
        [Description("'tool' (default) or 'role'")] string groupBy = "tool",
        McpServer server = null!,
        CancellationToken ct = default)
    {
        await workspaceRootResolver.EnsureResolvedAsync(server, ct);

        bool byRole = string.Equals(groupBy, "role", StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<SavingsAggregate> rows = await savings.GetReportAsync(_config.WorkspaceRoot, sinceDays, byRole, ct);

        var results = rows.Select(r =>
        {
            SavingsVerdict v = savings.Verdict(r);
            return new
            {
                name = r.Name,
                calls = r.Calls,
                raw_tokens = r.RawTokens,
                distilled_tokens = r.DistilledTokens,
                saved_tokens = r.SavedTokens,
                pct_saved = r.PctSaved,
                avg_duration_ms = r.AvgDurationMs,
                avg_conversation_turns = r.AvgConversationTurns,
                avg_conversation_tokens = r.AvgConversationTokens,
                worth_it = v.WorthIt,
                reason = v.Reason
            };
        });

        return JsonSerializer.Serialize(new { since_days = sinceDays, ranked_by = "saved_tokens", results });
    }

    // ── WorthKeeping ─────────────────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Direct yes/no/unknown verdict for one tool or role's distillation: is it saving enough tokens per " +
        "millisecond spent to be worth keeping on? Use TokenSavingsReport for the full ranked picture instead " +
        "if you want more than one target.")]
    public async Task<string> WorthKeeping(
        [Description("Tool or role name to judge, e.g. 'runTests' or 'test_failure'")]
        string target,
        [Description("'tool' (default) or 'role' — which column target matches")]
        string groupBy = "tool",
        [Description("Lookback window in days (default 30)")]
        int sinceDays = 30,
        McpServer server = null!,
        CancellationToken ct = default)
    {
        await workspaceRootResolver.EnsureResolvedAsync(server, ct);

        bool byRole = string.Equals(groupBy, "role", StringComparison.OrdinalIgnoreCase);
        SavingsAggregate row = await savings.GetOneAsync(_config.WorkspaceRoot, target, byRole, sinceDays, ct);

        if (row.Calls == 0)
        {
            return JsonSerializer.Serialize(new { target, worth_it = (bool?)null, reason = "no data for this target" });
        }

        SavingsVerdict v = savings.Verdict(row);
        return JsonSerializer.Serialize(new
        {
            target,
            calls = row.Calls,
            raw_tokens = row.RawTokens,
            distilled_tokens = row.DistilledTokens,
            saved_tokens = row.SavedTokens,
            pct_saved = row.PctSaved,
            avg_duration_ms = row.AvgDurationMs,
            avg_conversation_turns = row.AvgConversationTurns,
            avg_conversation_tokens = row.AvgConversationTokens,
            worth_it = v.WorthIt,
            reason = v.Reason
        });
    }
}
