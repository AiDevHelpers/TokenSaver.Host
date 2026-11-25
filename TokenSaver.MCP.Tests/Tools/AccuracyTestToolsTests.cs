#pragma warning disable CA2000 // Test factories hand disposables to the tool under test; each test is short-lived.
using AiDevTools.TokenSaver.Common.Core.Configuration;
using AiDevTools.TokenSaver.CodeHelper.Indexing;
using AiDevTools.TokenSaver.MCP.Tools;
using AiDevTools.TokenSaver.OllamaIntegration.Services;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Xunit;

namespace AiDevTools.TokenSaver.MCP.Tests.Tools;

/// <summary>
///     Covers AccuracyTestTools on its input-validation and setup-question branches. The paths that
///     scan the workspace or call the SimpleModelServer admin API (test with confirmed=true, get, and a
///     valid update) are not reached: the HTTP client points at an unreachable loopback port and the
///     scanner and accuracy-test service are passed as null because no test here reaches them.
/// </summary>
public sealed class AccuracyTestToolsTests
{
    private static AccuracyTestTools CreateTools(string workspaceRoot = "F:\\workspace")
    {
        TokenSaverConfig cfg = new() { WorkspaceRoot = workspaceRoot };
        HttpClient http = new() { BaseAddress = new Uri("http://127.0.0.1:1") };
        AgentConfigService agentConfig = new(http, Options.Create(cfg), NullLogger<AgentConfigService>.Instance);
        WorkspaceConfigManager workspaceIndex = new(Options.Create(cfg), NullLogger<WorkspaceConfigManager>.Instance);
        return new AccuracyTestTools(
            scanner: null!,
            tester: null!,
            agentConfig: agentConfig,
            workspaceIndex: workspaceIndex,
            workspaceRootResolver: WorkspaceRootSessionResolverTestFactory.Create(cfg));
    }

    [Fact]
    public async Task AgentConfig_TestNotConfirmed_ReturnsSetupQuestionsWithWorkspaceRoot()
    {
        AccuracyTestTools tools = CreateTools("F:\\my-ws");

        string result = await tools.AgentConfig("test");

        Assert.StartsWith("Before running the TokenSaver Tests, ask the user these setup questions:", result, StringComparison.Ordinal);
        Assert.Contains("(current: F:\\my-ws)", result, StringComparison.Ordinal);
        Assert.Contains("confirmed: true", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentConfig_UnknownAction_ReturnsUnknownActionMessage()
    {
        AccuracyTestTools tools = CreateTools();

        string result = await tools.AgentConfig("reboot");

        Assert.Equal("Unknown action. Use 'test', 'get', 'update', or 'save'.", result);
    }

    [Fact]
    public async Task AgentConfig_UpdateWithoutAgentType_ReturnsRequiredMessage()
    {
        AccuracyTestTools tools = CreateTools();

        string result = await tools.AgentConfig("update", temperature: 0.2);

        Assert.Equal("updateAgentType is required for action 'update'.", result);
    }

    [Fact]
    public async Task AgentConfig_UpdateWithoutAnyChange_ReturnsProvideAtLeastOne()
    {
        AccuracyTestTools tools = CreateTools();

        string result = await tools.AgentConfig("update", updateAgentType: "code");

        Assert.Equal("Provide at least one of systemPrompt, temperature, or topP.", result);
    }

    [Fact]
    public async Task AgentConfig_SaveWithoutProposals_ReturnsRequiredMessage()
    {
        AccuracyTestTools tools = CreateTools();

        string result = await tools.AgentConfig("save");

        Assert.Equal("proposalsJson is required for action 'save'.", result);
    }

    [Fact]
    public async Task AgentConfig_SaveEmptyObject_ReturnsNoProposalsMessage()
    {
        AccuracyTestTools tools = CreateTools();

        string result = await tools.AgentConfig("save", proposalsJson: "{}");

        Assert.Equal("No proposals found in the provided JSON.", result);
    }

    [Fact]
    public async Task AgentConfig_SaveMalformedJson_ReturnsParseFailure()
    {
        AccuracyTestTools tools = CreateTools();

        string result = await tools.AgentConfig("save", proposalsJson: "{not json");

        Assert.StartsWith("Failed to parse proposalsJson:", result, StringComparison.Ordinal);
    }
}
