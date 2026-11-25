using AiDevHelpers.AgentHelpers.Anthropic;
using AiDevHelpers.AgentHelpers.Core.Contracts;
using AiDevHelpers.AgentHelpers.Core.Models;
using AiDevHelpers.AgentHelpers.DeepSeek;
using AiDevHelpers.AgentHelpers.OpenSource;
using AiDevHelpers.AgentHelpers.Qwen;

using Xunit;

namespace AiDevTools.TokenSaver.MCP.Tests.AgentHelpers;

/// <summary>
///     One behavioural test set per adapter: what it claims, which callers it accepts and refuses, and that the format
///     it returns is usable. Detection is by client name or model hint only, so no network or file is involved.
/// </summary>
public sealed class AgentAdapterTests
{
    public static TheoryData<IAgentAdapter, string, AdapterKind, int> Identities => new()
    {
        { new AnthropicAgentAdapter(), "Anthropic", AdapterKind.Model, 100 },
        { new QwenAgentAdapter(), "Qwen", AdapterKind.Model, 80 },
        { new DeepSeekAgentAdapter(), "DeepSeek", AdapterKind.Model, 80 },
        { new ContinueClientAdapter(), "Continue", AdapterKind.Client, 90 },
        { new CopilotClientAdapter(), "Copilot", AdapterKind.Client, 90 },
    };

    [Theory]
    [MemberData(nameof(Identities))]
    public void EachAdapterDeclaresItsFamilyKindAndPriority(IAgentAdapter adapter, string family, AdapterKind kind, int priority)
    {
        Assert.Equal(family, adapter.Family);
        Assert.Equal(kind, adapter.Kind);
        Assert.Equal(priority, adapter.Priority);
    }

    [Theory]
    [InlineData("claude-code")]
    [InlineData("Claude-Desktop")]
    [InlineData("cowork")]
    public void Anthropic_AcceptsItsClientNamesCaseInsensitively(string client)
    {
        Assert.True(new AnthropicAgentAdapter().CanHandle(new CallerInfo { ClientName = client }));
    }

    [Theory]
    [InlineData("claude-sonnet-5-5")]
    [InlineData("Opus")]
    [InlineData("haiku-5")]
    public void Anthropic_AcceptsAClaudeModelHint(string hint)
    {
        Assert.True(new AnthropicAgentAdapter().CanHandle(new CallerInfo { ModelHint = hint }));
    }

    [Fact]
    public void Anthropic_RefusesAnUnrelatedCaller()
    {
        Assert.False(new AnthropicAgentAdapter().CanHandle(new CallerInfo { ClientName = "cursor", ModelHint = "gpt-4o" }));
    }

    [Theory]
    [InlineData("qwen2.5-coder")]
    [InlineData("Qwen3-thinking")]
    public void Qwen_AcceptsQwenModelHints(string hint)
    {
        Assert.True(new QwenAgentAdapter().CanHandle(new CallerInfo { ModelHint = hint }));
    }

    [Fact]
    public void Qwen_RefusesWithoutAModelHint()
    {
        Assert.False(new QwenAgentAdapter().CanHandle(new CallerInfo { ClientName = "claude-code" }));
    }

    [Fact]
    public void DeepSeek_AcceptsItsHintAndRefusesOthers()
    {
        DeepSeekAgentAdapter adapter = new();

        Assert.True(adapter.CanHandle(new CallerInfo { ModelHint = "deepseek-coder-v2" }));
        Assert.False(adapter.CanHandle(new CallerInfo { ModelHint = "qwen2.5" }));
    }

    [Theory]
    [InlineData("continue")]
    [InlineData("Continue.dev")]
    public void Continue_AcceptsItsClientNames(string client)
    {
        Assert.True(new ContinueClientAdapter().CanHandle(new CallerInfo { ClientName = client }));
    }

    [Fact]
    public void Copilot_AcceptsItsClientNameAndRefusesEmptyNames()
    {
        CopilotClientAdapter adapter = new();

        Assert.True(adapter.CanHandle(new CallerInfo { ClientName = "GitHub Copilot" }) || adapter.CanHandle(new CallerInfo { ClientName = "copilot" }));
        Assert.False(adapter.CanHandle(new CallerInfo { ClientName = "" }));
    }

    [Theory]
    [MemberData(nameof(Identities))]
    public void EveryAdapterReturnsAUsableFormatForAnyCaller(IAgentAdapter adapter, string family, AdapterKind kind, int priority)
    {
        _ = family;
        _ = kind;
        _ = priority;

        ContextFormat format = adapter.GetFormat(new CallerInfo { ClientName = "unknown", ModelHint = "unknown" });

        Assert.True(format.ContextTokenBudget > 0);
        Assert.True(format.PerFileTokenLimit >= 0); // 0 means no enforced per-file limit
    }

    [Theory]
    [InlineData("QWEN3-coder", 100_000)]
    [InlineData("qwq-32b", 500_000)]
    [InlineData("Qwen3.5-plus", 500_000)]
    [InlineData("qwen2.5-coder", 100_000)]
    public void Qwen_BudgetFollowsTheVariantRegardlessOfCase(string hint, int budget)
    {
        Assert.Equal(budget, new QwenAgentAdapter().GetFormat(new CallerInfo { ModelHint = hint }).ContextTokenBudget);
    }

    [Fact]
    public void Qwen_GivesThinkingModelsAMuchLargerBudget()
    {
        QwenAgentAdapter qwen = new();

        int plain = qwen.GetFormat(new CallerInfo { ModelHint = "qwen2.5-coder" }).ContextTokenBudget;
        int thinking = qwen.GetFormat(new CallerInfo { ModelHint = "qwen3-thinking" }).ContextTokenBudget;

        Assert.True(thinking > plain, $"thinking={thinking} plain={plain}");
    }
}
