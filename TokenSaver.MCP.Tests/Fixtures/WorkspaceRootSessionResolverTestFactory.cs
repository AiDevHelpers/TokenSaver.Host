#pragma warning disable CA1506 // Test factories wire many services together by design; the coupling is intentional.
#pragma warning disable CA2000 // Test factories hand disposables to the code under test; each test is short-lived.
using TokenSaver.CodeHelper.Configuration;
using TokenSaver.CodeHelper.Indexing;
using TokenSaver.Config;
using TokenSaver.ContextHelper.Calibration;
using TokenSaver.ContextHelper.Configuration;
using TokenSaver.ContextHelper.Pipeline;
using TokenSaver.ContextHelper.Pipeline.Stages;
using TokenSaver.Database.Context;
using TokenSaver.Common.Core.Configuration;
using TokenSaver.MCP.Services;
using TokenSaver.OllamaIntegration.Services;
using TokenSaver.TokenEconomy.Contracts;
using TokenSaver.TokenEconomy.Services;
using TokenSaver.Utilities.Services;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace TokenSaver.MCP.Tests;

/// <summary>
///     Builds a real (not mocked — no mocking library per repo convention), fully-wired
///     <see cref="WorkspaceRootSessionResolver" /> for tool-class tests that now take it as a
///     constructor dependency. Every test workspace already has <see cref="TokenSaverConfig.WorkspaceRoot" />
///     set up front, so <see cref="WorkspaceRootSessionResolver.EnsureResolvedAsync" /> always takes
///     its "already known" short-circuit path and never touches the <c>server</c> parameter or the
///     network — the HttpClient instances below use the same unreachable-loopback placeholder
///     TokenEconomyToolsTests/ContextDiagnosticsToolsTests use elsewhere in this project.
///     <see cref="ContextHelperConfig.AutoWarmOnStartup" /> is forced off so the background
///     warm-up task doesn't spend up to 10 minutes polling a SimpleModelServer that will never answer,
///     and <see cref="WorkspaceRootSessionResolver.SkipBackgroundWarmupForTests" /> is set so the
///     (unconditional, not gated by AutoWarmOnStartup) background indexing pass never actually
///     touches disk either — otherwise every test that calls a workspace-scoped tool method would
///     kick off a real index of that test's temp workspace, racing the test's own assertions.
/// </summary>
internal static class WorkspaceRootSessionResolverTestFactory
{
    private static HttpClient DeadClient() => new() { BaseAddress = new Uri("http://127.0.0.1:1") };

    public static WorkspaceRootSessionResolver Create(TokenSaverConfig config)
    {
        IOptions<TokenSaverConfig> configOptions = Options.Create(config);
        IOptions<ContextHelperConfig> contextHelperOptions =
            Options.Create(new ContextHelperConfig { AutoWarmOnStartup = false, EmbeddingModel = "" });

        WorkspaceIndexDbAccessor accessor = new(NullLogger<WorkspaceIndexDbAccessor>.Instance);
        GitStalenessChecker gitStalenessChecker = new(NullLogger<GitStalenessChecker>.Instance);
        GitStalenessService gitStaleness = new(accessor, gitStalenessChecker, NullLogger<GitStalenessService>.Instance);
        WorkspaceConfigManager workspaceConfig = new(configOptions, NullLogger<WorkspaceConfigManager>.Instance);
        FolderTreeManager folderTree = new(accessor, workspaceConfig, NullLogger<FolderTreeManager>.Instance);
        AiSummarizationService aiSummarization = new(NullLogger<AiSummarizationService>.Instance);
        SimpleModelServerCapacityClient capacityClient = new(DeadClient(), NullLogger<SimpleModelServerCapacityClient>.Instance);

        WorkspaceIndexService indexService = new(
            Options.Create(new CodeHelperConfig()), [], accessor, gitStaleness, folderTree,
            aiSummarization, capacityClient, NullLogger<WorkspaceIndexService>.Instance);

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

        OllamaService ollama = new(DeadClient(), configOptions, NullLogger<OllamaService>.Instance);
        ModelRouteResolver modelRoutes = new(configOptions);

        TokenEconomyDbAccessor tokenEconomyDb = new();
        ISavingsTracker savings = new SavingsTracker(tokenEconomyDb, configOptions);
        IRepoIndexer repoIndexer = new RepoIndexer(
            new ChunkExtractor(), ollama, modelRoutes, tokenEconomyDb, configOptions);
        SemanticIndexBackgroundTrigger semanticIndexTrigger = new(
            repoIndexer, tokenEconomyDb, NullLogger<SemanticIndexBackgroundTrigger>.Instance);

        ContextPipelineService contextPipeline = new(
            accessor, cleaner, workspaceConfig, indexService, semanticIndexTrigger,
            savings, calibration, new BuildArtifactDetector(NullLogger<BuildArtifactDetector>.Instance),
            new BehaviorConfigService(NullLogger<BehaviorConfigService>.Instance), configOptions);

        BehaviorConfigOverlay behaviorConfigOverlay = new(
            new BehaviorConfigService(NullLogger<BehaviorConfigService>.Instance),
            Options.Create(new CodeHelperConfig()), configOptions, contextHelperOptions);

        InstructionOptimizerService instructionOptimizer = new(
            new BehaviorConfigService(NullLogger<BehaviorConfigService>.Instance),
            new InstructionCacheService(NullLogger<InstructionCacheService>.Instance),
            NullLogger<InstructionOptimizerService>.Instance);

        return new WorkspaceRootSessionResolver(
            configOptions, contextHelperOptions, indexService, contextPipeline, ollama,
            behaviorConfigOverlay, instructionOptimizer, semanticIndexTrigger, new WorkspaceSessionRoots(),
            NullLogger<WorkspaceRootSessionResolver>.Instance)
        {
            SkipBackgroundWarmupForTests = true
        };
    }
}
