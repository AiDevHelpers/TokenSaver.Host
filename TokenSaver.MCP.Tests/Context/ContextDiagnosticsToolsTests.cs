#pragma warning disable CA1506 // Test factories wire many services together by design; the coupling is intentional.
#pragma warning disable CA2000 // Test factories hand disposables to the code under test; each test is short-lived.
using AiDevTools.TokenSaver.CodeHelper.Configuration;
using AiDevTools.TokenSaver.CodeHelper.Indexing;
using AiDevTools.TokenSaver.Config;
using AiDevTools.TokenSaver.ContextHelper.Calibration;
using AiDevTools.TokenSaver.ContextHelper.Configuration;
using AiDevTools.TokenSaver.ContextHelper.Models.Diagnostics;
using AiDevTools.TokenSaver.ContextHelper.Pipeline;
using AiDevTools.TokenSaver.ContextHelper.Pipeline.Stages;
using AiDevTools.TokenSaver.Database.Context;
using AiDevTools.TokenSaver.Database.Context.Entities;
using AiDevTools.TokenSaver.Common.Core.Configuration;
using AiDevTools.TokenSaver.MCP.Services;
using AiDevTools.TokenSaver.MCP.Tools;
using AiDevTools.TokenSaver.OllamaIntegration.Services;
using AiDevTools.TokenSaver.OllamaIntegration.Tests;
using AiDevTools.TokenSaver.TokenEconomy.Contracts;
using AiDevTools.TokenSaver.TokenEconomy.Services;
using AiDevTools.TokenSaver.Utilities.Services;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Xunit;

namespace AiDevTools.TokenSaver.MCP.Tests;

/// <summary>
///     Closes the "zero tests" gap CLAUDE.md flags for ContextDiagnosticsTools. All four
///     dependencies (ContextCleanerService, ContextPipelineService, FolderTreeManager,
///     WorkspaceConfigManager) are constructed for real against a temp workspace — no mocking
///     library per repo convention. HTTP-owning pieces (PromptAssessor, SemanticRelevanceFilterStage,
///     ConversationSummarizationStage, TokenBudgetStage) get the same
///     "new HttpClient { BaseAddress = http://localhost:1 }" placeholder TokenEconomyToolsTests
///     uses — none of the code paths exercised here (describe/stats/block/file/index/gitignore*)
///     ever reach the network: EmbeddingModel is set to "" to fast-skip the semantic filter, no
///     history is supplied so ConversationSummarization has nothing to summarize, and the "block"
///     happy path is driven by a real CleanContextAsync call whose prompt keyword-matches strongly
///     enough (Tier-1 confidence >= threshold) to skip PromptAssessor's Tier-2 model fallback.
///     The self-test cases are the exception: RunSelfTestAsync's synthetic payload is guaranteed
///     to shrink via LogStrippingStage alone, so those tests tolerate (and don't need) the
///     embedding/summarization stages' DeadClient HTTP calls failing — those stages degrade
///     gracefully rather than throwing.
/// </summary>
public sealed class ContextDiagnosticsToolsTests : IDisposable
{
    private readonly string _workspaceRoot;

    public ContextDiagnosticsToolsTests()
    {
        _workspaceRoot = Directory.CreateTempSubdirectory("cdtTests-").FullName;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspaceRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static HttpClient DeadClient() => new() { BaseAddress = new Uri("http://localhost:1") };

    private sealed record Rig(
        ContextDiagnosticsTools Tools,
        ContextCleanerService Cleaner,
        WorkspaceIndexDbAccessor Accessor,
        string WorkspaceRoot);

    private ContextDiagnosticsTools CreateTools(string? workspaceRoot = null, bool debug = false)
    {
        return CreateRig(workspaceRoot, debug).Tools;
    }

    private Rig CreateRig(string? workspaceRoot = null, bool debug = false)
    {
        string root = workspaceRoot ?? _workspaceRoot;

        ContextHelperConfig contextHelperConfig = new() { EmbeddingModel = "", Debug = debug };
        IOptions<ContextHelperConfig> contextHelperOptions = Options.Create(contextHelperConfig);

        RetrievalCalibrationService calibration = new(DeadClient(), contextHelperOptions, NullLogger<RetrievalCalibrationService>.Instance);

        PromptAssessor assessor = new(DeadClient(), contextHelperOptions, NullLogger<PromptAssessor>.Instance);
        StrippingHealthAnalyzer healthAnalyzer = new(contextHelperOptions, NullLogger<StrippingHealthAnalyzer>.Instance);

        DeduplicationStage stage01 = new(contextHelperOptions, NullLogger<DeduplicationStage>.Instance);
        LogStrippingStage stage02 = new(contextHelperOptions, NullLogger<LogStrippingStage>.Instance);
        CodeBlockCollapsingStage stage03 = new(contextHelperOptions, NullLogger<CodeBlockCollapsingStage>.Instance);
        ConversationSummarizationStage stage04 = new(DeadClient(), contextHelperOptions, NullLogger<ConversationSummarizationStage>.Instance);
        ToolCallCompressionStage stage05 = new(NullLogger<ToolCallCompressionStage>.Instance);
        SemanticRelevanceFilterStage stage06 = new(DeadClient(), contextHelperOptions, NullLogger<SemanticRelevanceFilterStage>.Instance, calibration);
        TokenBudgetStage stage07 = new(DeadClient(), contextHelperOptions, NullLogger<TokenBudgetStage>.Instance);
        ChunkingStage stage08 = new(contextHelperOptions, NullLogger<ChunkingStage>.Instance);
        ConversationalGarbageStage stage09 = new(NullLogger<ConversationalGarbageStage>.Instance);
        FormattingNormalizationStage stage10 = new(NullLogger<FormattingNormalizationStage>.Instance);

        ContextCleanerService cleaner = new(
            assessor, healthAnalyzer, contextHelperOptions, NullLogger<ContextCleanerService>.Instance,
            stage01, stage02, stage03, stage04, stage05, stage06, stage07, stage08, stage09, stage10);

        TokenSaverConfig tokenSaverConfig = new() { WorkspaceRoot = root };
        IOptions<TokenSaverConfig> tokenSaverOptions = Options.Create(tokenSaverConfig);

        WorkspaceConfigManager workspaceConfig = new(tokenSaverOptions, NullLogger<WorkspaceConfigManager>.Instance);
        WorkspaceIndexDbAccessor accessor = new(NullLogger<WorkspaceIndexDbAccessor>.Instance);
        FolderTreeManager folderTree = new(accessor, workspaceConfig, NullLogger<FolderTreeManager>.Instance);
        GitStalenessChecker gitStalenessChecker = new(NullLogger<GitStalenessChecker>.Instance);
        GitStalenessService gitStaleness = new(accessor, gitStalenessChecker, NullLogger<GitStalenessService>.Instance);

        CodeHelperConfig codeHelperConfig = new();
        AiSummarizationService aiSummarization = new(NullLogger<AiSummarizationService>.Instance);
        SimpleModelServerCapacityClient capacityClient = new(DeadClient(), NullLogger<SimpleModelServerCapacityClient>.Instance);
        WorkspaceIndexService orchestrator = new(
            Options.Create(codeHelperConfig), [], accessor, gitStaleness, folderTree,
            aiSummarization, capacityClient, NullLogger<WorkspaceIndexService>.Instance);

        OllamaService ollama = new(DeadClient(), tokenSaverOptions, NullLogger<OllamaService>.Instance);
        ModelRouteResolver modelRoutes = new(tokenSaverOptions);

        TokenEconomyDbAccessor tokenEconomyDb = new();
        ISavingsTracker savings = new SavingsTracker(tokenEconomyDb, tokenSaverOptions);
        IRepoIndexer repoIndexer = new RepoIndexer(
            new ChunkExtractor(), ollama, modelRoutes, tokenEconomyDb, tokenSaverOptions);
        SemanticIndexBackgroundTrigger semanticIndexTrigger = new(
            repoIndexer, tokenEconomyDb, NullLogger<SemanticIndexBackgroundTrigger>.Instance);

        ContextPipelineService contextPipeline = new(
            accessor, cleaner, workspaceConfig, orchestrator, semanticIndexTrigger,
            savings, calibration, new BuildArtifactDetector(NullLogger<BuildArtifactDetector>.Instance),
            new BehaviorConfigService(NullLogger<BehaviorConfigService>.Instance), tokenSaverOptions);

        BehaviorConfigOverlay behaviorConfigOverlay = new(
            new BehaviorConfigService(NullLogger<BehaviorConfigService>.Instance),
            Options.Create(codeHelperConfig), tokenSaverOptions, contextHelperOptions);
        InstructionOptimizerService instructionOptimizer = new(
            new BehaviorConfigService(NullLogger<BehaviorConfigService>.Instance),
            new InstructionCacheService(NullLogger<InstructionCacheService>.Instance),
            NullLogger<InstructionOptimizerService>.Instance);
        WorkspaceRootSessionResolver workspaceRootResolver = new(
            tokenSaverOptions, contextHelperOptions, orchestrator, contextPipeline, ollama,
            behaviorConfigOverlay, instructionOptimizer, semanticIndexTrigger, new WorkspaceSessionRoots(),
            NullLogger<WorkspaceRootSessionResolver>.Instance)
        {
            SkipBackgroundWarmupForTests = true
        };

        // The index self-test step runs against a fake Ollama so it finishes without the network.
        IndexSelfTest indexSelfTest = new(
            new RepoIndexer(new ChunkExtractor(),
                new OllamaService(new HttpClient(new FakeOllamaHandler()) { BaseAddress = new Uri("http://fake-ollama.local") },
                    tokenSaverOptions, NullLogger<OllamaService>.Instance),
                modelRoutes, tokenEconomyDb, tokenSaverOptions),
            tokenEconomyDb);

        // The semantic-quality step gets a fake model that answers like one that understands the code.
        FakeOllamaHandler qualityHandler = new()
        {
            GenerateResponder = SemanticQualityFakes.GoodSummary,
            EmbedResponder = SemanticQualityFakes.BagOfWordsEmbed
        };
        OllamaService qualityOllama = new(
            new HttpClient(qualityHandler) { BaseAddress = new Uri("http://fake-ollama.local") },
            tokenSaverOptions, NullLogger<OllamaService>.Instance);
        SemanticQualitySelfTest semanticQualitySelfTest = new(
            new RepoIndexer(new ChunkExtractor(), qualityOllama, modelRoutes, tokenEconomyDb, tokenSaverOptions),
            new SemanticSearchService(qualityOllama, modelRoutes, tokenEconomyDb),
            tokenEconomyDb);

        ContextDiagnosticsTools tools = new(cleaner, contextPipeline, folderTree, workspaceConfig, workspaceRootResolver, calibration, indexSelfTest, semanticQualitySelfTest);
        return new Rig(tools, cleaner, accessor, root);
    }

    // ── describe ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Describe_NoContextBuilt_ReturnsNotBuiltMessage()
    {
        ContextDiagnosticsTools tools = CreateTools();

        string result = await tools.ContextDiag("describe", filter: null);

        Assert.Contains("No context has been built yet", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("code")]
    [InlineData("logs")]
    [InlineData("files")]
    [InlineData("conversation")]
    [InlineData("toolcalls")]
    [InlineData("src/Foo.cs")]
    public async Task Describe_WithFilter_NoContextBuilt_ReturnsNotBuiltMessage(string filter)
    {
        ContextDiagnosticsTools tools = CreateTools();

        string result = await tools.ContextDiag("describe", filter: filter);

        Assert.Contains("No context has been built yet", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Describe_AfterBuild_ReturnsNarrativeAndManifest()
    {
        Rig rig = CreateRig();
        await BuildFastContextAsync(rig.Cleaner);

        string result = await rig.Tools.ContextDiag("describe", filter: null);

        Assert.Contains("Context built in", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Describe_AfterBuild_UnmatchedFilter_ReturnsNoMatchMessage()
    {
        Rig rig = CreateRig();
        await BuildFastContextAsync(rig.Cleaner);

        string result = await rig.Tools.ContextDiag("describe", filter: "toolcalls");

        Assert.Contains("No context entries match filter", result, StringComparison.Ordinal);
    }

    // ── stats ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Stats_NoContextBuilt_ReturnsNoStatsMessage()
    {
        ContextDiagnosticsTools tools = CreateTools();

        string result = await tools.ContextDiag("stats");

        Assert.Contains("No pipeline stats available", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stats_AfterBuild_ReturnsStageTable()
    {
        Rig rig = CreateRig();
        await BuildFastContextAsync(rig.Cleaner);

        string result = await rig.Tools.ContextDiag("stats");

        Assert.Contains("Pipeline Stats", result, StringComparison.Ordinal);
        Assert.Contains("Stage", result, StringComparison.Ordinal);
    }

    // ── self-test ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SelfTest_DebugDisabled_IsHiddenBehindUnknownActionMessage()
    {
        // Default (debug=false): 'self-test' must be indistinguishable from a typo'd/unknown
        // action — no signal to the calling agent that this action exists at all.
        ContextDiagnosticsTools tools = CreateTools(debug: false);

        string result = await tools.ContextDiag("self-test");

        Assert.Contains("Unknown action", result, StringComparison.Ordinal);
        Assert.DoesNotContain("PASS", result, StringComparison.Ordinal);
        Assert.DoesNotContain("FAIL", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelfTest_DebugEnabled_RunsRealPipeline_ReportsPass()
    {
        ContextDiagnosticsTools tools = CreateTools(debug: true);

        string result = await tools.ContextDiag("self-test");

        Assert.Contains("PASS", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelfTest_DebugEnabled_IndexStepScansEveryKnownFileAndStoresEveryChunk()
    {
        ContextDiagnosticsTools tools = CreateTools(debug: true);

        string result = await tools.ContextDiag("self-test");

        Assert.Contains("✓ PASS — index: 3 of 3 known files scanned", result, StringComparison.Ordinal);
        Assert.Contains("failed 0", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelfTest_DebugEnabled_SemanticQualityStepReportsPass()
    {
        ContextDiagnosticsTools tools = CreateTools(debug: true);

        string result = await tools.ContextDiag("self-test");

        Assert.Contains("✓ PASS — semantic quality: 4 of 4", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelfTest_SyntheticPayload_ActuallyShrinks()
    {
        Rig rig = CreateRig(debug: true);

        ContextSelfTestResult result = await rig.Cleaner.RunSelfTestAsync();

        Assert.True(result.Passed);
        Assert.True(result.CleanedTokens < result.RawTokens);
    }

    // ── block ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Block_MissingId_ReturnsIdRequiredMessage()
    {
        ContextDiagnosticsTools tools = CreateTools();

        string result = await tools.ContextDiag("block", id: null);

        Assert.Contains("id is required", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Block_NoContextBuilt_ReturnsNoContextMessage()
    {
        ContextDiagnosticsTools tools = CreateTools();

        string result = await tools.ContextDiag("block", id: "deadbeef");

        Assert.Contains("No context available", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Block_AfterBuild_UnknownId_ReturnsNotFoundMessage()
    {
        Rig rig = CreateRig();
        await BuildFastContextAsync(rig.Cleaner);

        string result = await rig.Tools.ContextDiag("block", id: "nonexistent-id-xyz");

        Assert.Contains("not found in context", result, StringComparison.Ordinal);
    }

    // Driving the "found" branch requires a manifest entry whose ChunkId or SectionName matches —
    // those are populated internally by ChunkingStage from real file/code content passed through
    // BuildConciseContext's requestedFiles, which itself needs the full WorkspaceIndexService
    // orchestrator pipeline (AI summarization / symbol parsing) to behave realistically. That's
    // exactly the kind of live-Ollama-shaped path CLAUDE.md's "Known gaps" note excludes — skipped
    // here; not-found + missing-args + no-context-yet are covered above instead.

    // ── file ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task File_MissingId_ReturnsIdRequiredMessage()
    {
        ContextDiagnosticsTools tools = CreateTools();

        string result = await tools.ContextDiag("file", id: null);

        Assert.Contains("id is required", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task File_NotIndexed_ReturnsNotIndexedMessage()
    {
        ContextDiagnosticsTools tools = CreateTools();

        string result = await tools.ContextDiag("file", id: "docs/missing.md");

        Assert.Contains("has not been indexed yet", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task File_Indexed_ReturnsContentWithSummaryMarker()
    {
        string relPath = "docs/guide.md";
        string absPath = Path.Combine(_workspaceRoot, relPath);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(absPath)!);
        await File.WriteAllTextAsync(absPath, "# Guide\nSome short content.");

        await SeedFileRowAsync(_workspaceRoot, relPath, "A short guide");

        ContextDiagnosticsTools tools = CreateTools();

        string result = await tools.ContextDiag("file", id: relPath);

        Assert.Contains("tokensaver:summary-of:" + relPath, result, StringComparison.Ordinal);
        Assert.Contains("Some short content.", result, StringComparison.Ordinal);
    }

    // ── index ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Index_UnknownFolder_ReturnsNoIndexMessage()
    {
        ContextDiagnosticsTools tools = CreateTools();

        string result = await tools.ContextDiag("index", id: "does/not/exist");

        Assert.Contains("No index found for", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Index_RootWithNoFolderRow_ReturnsNoIndexMessage()
    {
        ContextDiagnosticsTools tools = CreateTools();

        string result = await tools.ContextDiag("index", id: null);

        Assert.Contains("No index found for root", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Index_RootFolderSeeded_ReturnsJson()
    {
        await SeedRootFolderAsync(_workspaceRoot);

        ContextDiagnosticsTools tools = CreateTools();

        string result = await tools.ContextDiag("index", id: null);

        Assert.Contains("\"path\": \".\"", result, StringComparison.Ordinal);
    }

    // ── gitignore ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Gitignore_UncoveredGeneratedDirs_ReportsIssues()
    {
        _ = Directory.CreateDirectory(Path.Combine(_workspaceRoot, "bin"));
        _ = Directory.CreateDirectory(Path.Combine(_workspaceRoot, "obj"));
        _ = Directory.CreateDirectory(Path.Combine(_workspaceRoot, "node_modules"));

        ContextDiagnosticsTools tools = CreateTools();

        string result = await tools.ContextDiag("gitignore", root: _workspaceRoot);

        Assert.Contains("CONFIRMED", result, StringComparison.Ordinal);
        Assert.Contains("bin/", result, StringComparison.Ordinal);
        Assert.Contains("obj/", result, StringComparison.Ordinal);
        Assert.Contains("node_modules/", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gitignore_AllCovered_ReportsClean()
    {
        _ = Directory.CreateDirectory(Path.Combine(_workspaceRoot, "bin"));
        await File.WriteAllTextAsync(Path.Combine(_workspaceRoot, ".gitignore"), "bin/\n");

        ContextDiagnosticsTools tools = CreateTools();

        string result = await tools.ContextDiag("gitignore", root: _workspaceRoot);

        Assert.Contains("No gitignore issues found", result, StringComparison.Ordinal);
    }

    // ── gitignore-add ────────────────────────────────────────────────────────

    [Fact]
    public async Task GitignoreAdd_NotConfirmed_LeavesGitignoreUnchanged()
    {
        _ = Directory.CreateDirectory(Path.Combine(_workspaceRoot, "bin"));
        ContextDiagnosticsTools tools = CreateTools();

        string result = await tools.ContextDiag("gitignore-add", confirmed: false, root: _workspaceRoot);

        Assert.Contains("Set confirmed=true", result, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_workspaceRoot, ".gitignore")));
    }

    [Fact]
    public async Task GitignoreAdd_Confirmed_AppendsEntries()
    {
        _ = Directory.CreateDirectory(Path.Combine(_workspaceRoot, "bin"));
        ContextDiagnosticsTools tools = CreateTools();

        string result = await tools.ContextDiag("gitignore-add", confirmed: true, root: _workspaceRoot);

        Assert.Contains("Added", result, StringComparison.Ordinal);
        string gitignorePath = Path.Combine(_workspaceRoot, ".gitignore");
        Assert.True(File.Exists(gitignorePath));
        string content = await File.ReadAllTextAsync(gitignorePath);
        Assert.Contains("bin/", content, StringComparison.Ordinal);
    }

    // ── context-exclude ──────────────────────────────────────────────────────

    [Fact]
    public async Task ContextExclude_NotConfirmed_ReturnsRejectedMessage()
    {
        ContextDiagnosticsTools tools = CreateTools();

        string result = await tools.ContextDiag(
            "context-exclude", confirmed: false, root: _workspaceRoot, paths: ["some/dir"]);

        Assert.Contains("Set confirmed=true", result, StringComparison.Ordinal);
        Assert.False(File.Exists(WorkspaceIgnoreFilter.AdditionalPath(_workspaceRoot)));
    }

    [Fact]
    public async Task ContextExclude_ConfirmedMissingPaths_ReturnsPathsRequiredMessage()
    {
        ContextDiagnosticsTools tools = CreateTools();

        string result = await tools.ContextDiag(
            "context-exclude", confirmed: true, root: _workspaceRoot, paths: null);

        Assert.Contains("paths[] is required", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContextExclude_ConfirmedWithPaths_AppendsToIgnoreAdditional()
    {
        ContextDiagnosticsTools tools = CreateTools();

        string result = await tools.ContextDiag(
            "context-exclude", confirmed: true, root: _workspaceRoot, paths: ["some/generated/dir"]);

        Assert.Contains("Appended", result, StringComparison.Ordinal);
        string filePath = WorkspaceIgnoreFilter.AdditionalPath(_workspaceRoot);
        Assert.True(File.Exists(filePath));
        string content = await File.ReadAllTextAsync(filePath);
        Assert.Contains("some/generated/dir/", content, StringComparison.Ordinal);
    }

    // ── unknown action ───────────────────────────────────────────────────────

    [Fact]
    public async Task UnknownAction_ReturnsUsageMessage()
    {
        ContextDiagnosticsTools tools = CreateTools();

        string result = await tools.ContextDiag("bogus-action");

        Assert.Contains("Unknown action", result, StringComparison.Ordinal);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    // Keyword-dense Debug-task prompt: Tier-1 keyword classification alone clears
    // PromptAssessorConfidenceThreshold (0.6 default), so PromptAssessor never falls back to its
    // Tier-2 model HTTP call. No history is passed, so ConversationSummarizationStage has nothing
    // to summarize either — the whole pipeline run stays fully offline.
    private static async Task BuildFastContextAsync(ContextCleanerService cleaner)
    {
        _ = await cleaner.CleanContextAsync(
            "fix the error exception crash stack trace not working fails failure broken");
    }

    private async Task SeedFileRowAsync(string workspaceRoot, string relPath, string description)
    {
        using WorkspaceIndexContext ctx = await WorkspaceIndexContextFactory.CreateAsync(workspaceRoot);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        FolderEntity folder = new()
        {
            RelativePath = ".",
            ParentFolderId = null,
            Description = "root",
            LastModified = now,
            UpdatedAt = now
        };
        _ = ctx.Folders.Add(folder);
        _ = await ctx.SaveChangesAsync();

        FileEntity file = new()
        {
            FolderId = folder.Id,
            RelativePath = relPath,
            FileType = "markdown",
            Description = description,
            ContentHash = "seed",
            Size = 0,
            LastModified = now,
            UpdatedAt = now
        };
        _ = ctx.Files.Add(file);
        _ = await ctx.SaveChangesAsync();
    }

    private async Task SeedRootFolderAsync(string workspaceRoot)
    {
        using WorkspaceIndexContext ctx = await WorkspaceIndexContextFactory.CreateAsync(workspaceRoot);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        FolderEntity folder = new()
        {
            RelativePath = ".",
            ParentFolderId = null,
            Description = "root folder",
            LastModified = now,
            UpdatedAt = now
        };
        _ = ctx.Folders.Add(folder);
        _ = await ctx.SaveChangesAsync();
    }
}
