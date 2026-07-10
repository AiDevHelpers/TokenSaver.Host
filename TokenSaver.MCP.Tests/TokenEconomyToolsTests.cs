using TokenSaver.Library.Configuration;
using TokenSaver.Database.Context;
using TokenSaver.Library.Services;
using TokenSaver.MCP.Services;
using TokenSaver.MCP.Tools;

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
        JsonElement result = JsonDocument.Parse(json).RootElement;

        Assert.False(result.GetProperty("over_budget").GetBoolean());
        Assert.Equal(8000, result.GetProperty("threshold").GetInt32());
    }

    [Fact]
    public void TokenBudget_OverThreshold_ReportsOverBudgetWithCorrectPct()
    {
        TokenEconomyTools tools = CreateTools();
        string content = new string('a', 4000); // ~1000 estimated tokens

        string json = tools.TokenBudget(content, threshold: 500);
        JsonElement result = JsonDocument.Parse(json).RootElement;

        Assert.True(result.GetProperty("over_budget").GetBoolean());
        Assert.Equal(1000, result.GetProperty("tokens").GetInt32());
        Assert.Equal(200, result.GetProperty("pct_of_threshold").GetInt32()); // 1000/500 = 200%
    }

    [Fact]
    public void TokenBudget_ZeroThreshold_DoesNotDivideByZero()
    {
        TokenEconomyTools tools = CreateTools();

        string json = tools.TokenBudget("anything", threshold: 0);
        JsonElement result = JsonDocument.Parse(json).RootElement;

        Assert.Equal(0, result.GetProperty("pct_of_threshold").GetInt32());
    }
}
