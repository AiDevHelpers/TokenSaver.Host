#pragma warning disable CA2000 // Test factories hand disposables to the tool under test; each test is short-lived.
using TokenSaver.Common.Core.Configuration;
using TokenSaver.CodeHelper.ContextSegmentation;
using TokenSaver.CodeHelper.Configuration;
using TokenSaver.CodeHelper.Indexing;
using TokenSaver.CodeHelper.Planning;
using TokenSaver.Config;
using TokenSaver.MCP.Tools;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Xunit;

namespace TokenSaver.MCP.Tests.Tools;

/// <summary>
///     Covers AdaptiveContextLoaderTools against real segmentation, pattern tracking and loader
///     services rooted in a temp workspace (no segments.json, so built-in segments are used). The
///     task classifier gets an unreachable HttpClient so it never calls a model. Session recording
///     writes only under the temp workspace.
/// </summary>
public sealed class AdaptiveContextLoaderToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tokensaver-adaptive-tests-" + Guid.NewGuid().ToString("N"));

    public AdaptiveContextLoaderToolsTests()
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

    private AdaptiveContextLoaderTools CreateTools()
    {
        TokenSaverConfig cfg = new() { WorkspaceRoot = _root };
        IOptions<CodeHelperConfig> codeOptions = Options.Create(new CodeHelperConfig());
        ContextSegmentationService segmentation = new(_root, new BehaviorConfigService(NullLogger<BehaviorConfigService>.Instance), NullLogger<ContextSegmentationService>.Instance);
        AccessPatternTracker tracker = new(_root, NullLogger<AccessPatternTracker>.Instance);
        TaskClassifier classifier = new(new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1") }, codeOptions, NullLogger<TaskClassifier>.Instance);
        AdaptiveContextLoader loader = new(segmentation, tracker, classifier, NullLogger<AdaptiveContextLoader>.Instance);
        return new AdaptiveContextLoaderTools(
            loader,
            segmentation,
            tracker,
            Options.Create(cfg),
            new WorkspaceConfigManager(Options.Create(cfg), NullLogger<WorkspaceConfigManager>.Instance),
            WorkspaceRootSessionResolverTestFactory.Create(cfg),
            NullLogger<AdaptiveContextLoaderTools>.Instance);
    }

    [Fact]
    public async Task AnalyzeContextSegmentation_UnknownAction_ReturnsUnknownAction()
    {
        AdaptiveContextLoaderTools tools = CreateTools();

        string result = await tools.AnalyzeContextSegmentation("explode");

        Assert.Equal("Unknown action. Use 'segments', 'patterns', or 'recommend'.", result);
    }

    [Fact]
    public async Task AnalyzeContextSegmentation_Segments_ListsDefinedSegments()
    {
        AdaptiveContextLoaderTools tools = CreateTools();

        string result = await tools.AnalyzeContextSegmentation("segments");

        Assert.StartsWith("Defined Segments (", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnalyzeContextSegmentation_PatternsWithNoHistory_ReportsNoPatterns()
    {
        AdaptiveContextLoaderTools tools = CreateTools();

        string result = await tools.AnalyzeContextSegmentation("patterns");

        Assert.StartsWith("No access patterns recorded yet.", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnalyzeContextSegmentation_RecommendWithoutPrompt_ReturnsPromptRequired()
    {
        AdaptiveContextLoaderTools tools = CreateTools();

        string result = await tools.AnalyzeContextSegmentation("recommend", prompt: " ");

        Assert.Equal("prompt is required for action 'recommend'.", result);
    }

    [Fact]
    public async Task BuildAdaptiveContext_PromptWithForcedSegment_ReturnsPlanWithSessionId()
    {
        AdaptiveContextLoaderTools tools = CreateTools();

        string result = await tools.BuildAdaptiveContext(
            "fix the failing parser test",
            sessionId: "sess-1",
            tokenBudget: 20000,
            forceIncludeSegments: []);

        Assert.StartsWith("Adaptive Context Load Plan", result, StringComparison.Ordinal);
        Assert.Contains("Session ID for tracking:", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordContextSession_MissingSessionId_ReturnsRequired()
    {
        AdaptiveContextLoaderTools tools = CreateTools();

        string result = await tools.RecordContextSession(" ");

        Assert.Equal("sessionId is required.", result);
    }

    [Fact]
    public async Task RecordContextSession_UnknownOutcome_ReturnsOutcomeError()
    {
        AdaptiveContextLoaderTools tools = CreateTools();

        string result = await tools.RecordContextSession("sess-2", outcome: "Maybe");

        Assert.Equal("Unknown outcome 'Maybe'. Use 'Succeeded', 'Failed', or 'Abandoned'.", result);
    }

    [Fact]
    public async Task RecordContextSession_ValidOutcome_RecordsAndReportsCounts()
    {
        AdaptiveContextLoaderTools tools = CreateTools();

        string result = await tools.RecordContextSession(
            "sess-3",
            segmentsUsed: ["seg-a", "seg-b"],
            segmentsModified: ["seg-a"],
            outcome: "Failed");

        Assert.StartsWith("Session sess-3 recorded: Failed", result, StringComparison.Ordinal);
        Assert.Contains("Used: 2 segments | Modified: 1 segments", result, StringComparison.Ordinal);
    }
}
