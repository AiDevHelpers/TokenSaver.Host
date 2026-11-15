#pragma warning disable CA2000 // Test factories hand disposables to the tool under test; each test is short-lived.
using TokenSaver.Common.Core.Configuration;
using TokenSaver.Database.Context;
using TokenSaver.MCP.Services;
using TokenSaver.MCP.Tools;
using TokenSaver.OllamaIntegration.Services;
using TokenSaver.TokenEconomy.Services;
using TokenSaver.Utilities.Services;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using System.Text.Json;

using Xunit;

namespace TokenSaver.MCP.Tests;

/// <summary>
///     TokenBudget is the one method on TokenEconomyTools that never calls Ollama (pure
///     EstimateTokens math), so it's testable end-to-end through the actual tool class without
///     a live model — exactly the gap CLAUDE.md's "Known gaps" section flags for every existing
///     Tools/*.cs class (zero tests today). ModelRoute/TokenSavingsReport/WorthKeeping need a
///     live Ollama process and/or a populated savings history and are covered instead at the
///     service level (DistillationServiceTests, SavingsTrackerTests in TokenSaver.Library.Tests).
/// </summary>
public class TokenEconomyToolsTests
{
    private static TokenEconomyTools CreateTools(TokenSaverConfig? config = null)
    {
        TokenSaverConfig cfg = config ?? new TokenSaverConfig();
        HttpClient httpClient = new() { BaseAddress = new Uri("http://localhost:1") };
        OllamaService ollama = new(httpClient, Options.Create(cfg), NullLogger<OllamaService>.Instance);
        ModelRouteResolver modelRoutes = new(Options.Create(cfg));
        SavingsTracker savings = new(new TokenEconomyDbAccessor(), Options.Create(cfg));
        DistillationService distill = new(ollama, modelRoutes, savings, Options.Create(cfg));
        WorkspaceRootSessionResolver workspaceRootResolver = WorkspaceRootSessionResolverTestFactory.Create(cfg);
        return new TokenEconomyTools(distill, savings, modelRoutes, ollama, Options.Create(cfg), workspaceRootResolver);
    }

    [Fact]
    public void TokenBudget_UnderThreshold_ReportsNotOverBudget()
    {
        TokenEconomyTools tools = CreateTools();

        string json = tools.TokenBudget("short content", threshold: 8000);
        JsonElement result = JsonElement.Parse(json);

        Assert.False(result.GetProperty("over_budget").GetBoolean());
        Assert.Equal(8000, result.GetProperty("threshold").GetInt32());
    }

    [Fact]
    public void TokenBudget_OverThreshold_ReportsOverBudgetWithCorrectPct()
    {
        TokenEconomyTools tools = CreateTools();
        string content = new string('a', 4000); // ~1000 estimated tokens

        string json = tools.TokenBudget(content, threshold: 500);
        JsonElement result = JsonElement.Parse(json);

        Assert.True(result.GetProperty("over_budget").GetBoolean());
        Assert.Equal(1000, result.GetProperty("tokens").GetInt32());
        Assert.Equal(200, result.GetProperty("pct_of_threshold").GetInt32()); // 1000/500 = 200%
    }

    [Fact]
    public void TokenBudget_ZeroThreshold_DoesNotDivideByZero()
    {
        TokenEconomyTools tools = CreateTools();

        string json = tools.TokenBudget("anything", threshold: 0);
        JsonElement result = JsonElement.Parse(json);

        Assert.Equal(0, result.GetProperty("pct_of_threshold").GetInt32());
    }

    [Fact]
    public async Task ModelRoute_OllamaUnreachable_ReturnsGuardMessage()
    {
        // Points at the unreachable loopback placeholder, so the call fails at connect time without
        // reaching a real model. The transport error is returned as a message, not thrown.
        TokenEconomyTools tools = CreateTools();

        string result = await tools.ModelRoute("code", "hello", maxTokens: 16);

        Assert.StartsWith("Model call failed for route 'code'", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TokenSavingsReport_EmptyWorkspace_ReturnsRankedEnvelope()
    {
        string root = Path.Combine(Path.GetTempPath(), "tokensaver-economy-report-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        try
        {
            TokenEconomyTools tools = CreateTools(new TokenSaverConfig { WorkspaceRoot = root });

            string json = await tools.TokenSavingsReport(sinceDays: 3, groupBy: "role");
            JsonElement result = JsonElement.Parse(json);

            Assert.Equal(3, result.GetProperty("since_days").GetInt32());
            Assert.Equal("saved_tokens", result.GetProperty("ranked_by").GetString());
            Assert.Equal(JsonValueKind.Array, result.GetProperty("results").ValueKind);
        }
        finally
        {
            DeleteBestEffort(root);
        }
    }

    [Fact]
    public async Task WorthKeeping_UnknownTarget_ReportsNoDataWithNullVerdict()
    {
        string root = Path.Combine(Path.GetTempPath(), "tokensaver-economy-worth-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        try
        {
            TokenEconomyTools tools = CreateTools(new TokenSaverConfig { WorkspaceRoot = root });

            string json = await tools.WorthKeeping("no-such-target-xyz");
            JsonElement result = JsonElement.Parse(json);

            Assert.Equal("no-such-target-xyz", result.GetProperty("target").GetString());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("worth_it").ValueKind);
            Assert.Equal("no data for this target", result.GetProperty("reason").GetString());
        }
        finally
        {
            DeleteBestEffort(root);
        }
    }

    /// <summary>The savings DB keeps index.db open in pooled SQLite connections, so the folder may stay locked until exit.</summary>
    private static void DeleteBestEffort(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
