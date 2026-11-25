using AiDevTools.TokenSaver.Common.Core.Configuration;
using AiDevTools.TokenSaver.CodeHelper.AutoWorkflow;
using AiDevTools.TokenSaver.CodeHelper.Calibration;
using AiDevTools.TokenSaver.CodeHelper.Configuration;
using AiDevTools.TokenSaver.MCP.Tools;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Xunit;

namespace AiDevTools.TokenSaver.MCP.Tests.Tools;

/// <summary>
///     Covers AutoWorkflowTools on its validation branches and on the Calibration toggle/stats/feedback
///     paths. CodeWork start/continue that would run the workflow (and therefore the local model) are
///     not reached: every CodeWork test returns before the workflow, cleaner, or agent registry is used,
///     so those constructor dependencies are passed as null and are never dereferenced here.
/// </summary>
public sealed class AutoWorkflowToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tokensaver-autoworkflow-tests-" + Guid.NewGuid().ToString("N"));

    public AutoWorkflowToolsTests()
    {
        _ = Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private AutoWorkflowTools CreateTools(WorkflowSessionStore? sessions = null)
    {
        TokenSaverConfig libCfg = new() { WorkspaceRoot = _root };
        CodeHelperConfig codeCfg = new();
        CalibrationService calibration = new(Options.Create(codeCfg), NullLogger<CalibrationService>.Instance);
        return new AutoWorkflowTools(
            workflow: null!,
            calibration: calibration,
            sessions: sessions ?? new WorkflowSessionStore(),
            libConfig: Options.Create(libCfg),
            codeConfig: Options.Create(codeCfg),
            cleaner: null!,
            detection: null!,
            registry: null!,
            httpContext: null!,
            workspaceRootResolver: WorkspaceRootSessionResolverTestFactory.Create(libCfg));
    }

    [Fact]
    public async Task CodeWork_UnknownAction_ReturnsUnknownActionMessage()
    {
        AutoWorkflowTools tools = CreateTools();

        string result = await tools.CodeWork("explode");

        Assert.Equal("Unknown action. Use 'start' or 'continue'.", result);
    }

    [Fact]
    public async Task CodeWork_StartWithoutPrompt_ReturnsPromptRequired()
    {
        AutoWorkflowTools tools = CreateTools();

        string result = await tools.CodeWork("start", prompt: "   ");

        Assert.Equal("prompt is required for action 'start'.", result);
    }

    [Fact]
    public async Task CodeWork_ContinueWithoutSessionId_ReturnsSessionRequired()
    {
        AutoWorkflowTools tools = CreateTools();

        string result = await tools.CodeWork("continue", skeleton: "design");

        Assert.Equal("sessionId is required for action 'continue'.", result);
    }

    [Fact]
    public async Task CodeWork_ContinueWithoutSkeleton_ReturnsSkeletonRequired()
    {
        AutoWorkflowTools tools = CreateTools();

        string result = await tools.CodeWork("CONTINUE", sessionId: "abc");

        Assert.Equal("skeleton is required for action 'continue'.", result);
    }

    [Fact]
    public async Task Calibration_ToggleWithoutEnabled_ReturnsRequiredMessage()
    {
        AutoWorkflowTools tools = CreateTools();

        string result = await tools.Calibration("toggle");

        Assert.Equal("enabled is required for action 'toggle'.", result);
    }

    [Fact]
    public async Task Calibration_ToggleWorkspaceScopeWithDisable_PersistsToWorkspaceFile()
    {
        AutoWorkflowTools tools = CreateTools();

        string result = await tools.Calibration("toggle", enabled: false, scope: "workspace");

        Assert.Equal("CodeHelper auto-workflow disabled (workspace, persisted).", result);
    }

    [Fact]
    public async Task Calibration_Stats_ReturnsCalibrationHeader()
    {
        AutoWorkflowTools tools = CreateTools();

        string result = await tools.Calibration("stats");

        Assert.StartsWith("## CodeHelper Calibration Stats", result, StringComparison.Ordinal);
        Assert.Contains("**Overall pass rate:**", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Calibration_FeedbackWithoutSessionId_ReturnsSessionRequired()
    {
        AutoWorkflowTools tools = CreateTools();

        string result = await tools.Calibration("feedback", aiFixCount: 1, aiFixSeverity: "Minor", correctionRatio: 0.1);

        Assert.Equal("sessionId is required for action 'feedback'.", result);
    }

    [Fact]
    public async Task Calibration_FeedbackWithoutCorrectionRatio_ReturnsRatioRequired()
    {
        AutoWorkflowTools tools = CreateTools();

        string result = await tools.Calibration("feedback", sessionId: "s1", aiFixCount: 1, aiFixSeverity: "Minor");

        Assert.Equal("correctionRatio is required for action 'feedback'.", result);
    }

    [Fact]
    public async Task Calibration_FeedbackUnknownSession_ReturnsNotFound()
    {
        AutoWorkflowTools tools = CreateTools();

        string result = await tools.Calibration("feedback", sessionId: "missing", aiFixCount: 1, aiFixSeverity: "Minor", correctionRatio: 0.1);

        Assert.Equal("❌ Session 'missing' not found.", result);
    }

    [Fact]
    public async Task Calibration_UnknownAction_ReturnsUnknownActionMessage()
    {
        AutoWorkflowTools tools = CreateTools();

        string result = await tools.Calibration("reset");

        Assert.Equal("Unknown action. Use 'toggle', 'stats', or 'feedback'.", result);
    }
}
