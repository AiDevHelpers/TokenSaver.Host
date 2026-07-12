using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TokenSaver.AgentHelpers.Core.Contracts;
using TokenSaver.AgentHelpers.Core.Models;
using TokenSaver.AgentHelpers.Core.Services;
using TokenSaver.CodeHelper.Configuration;
using TokenSaver.CodeHelper.Indexing;
using TokenSaver.Config;
using TokenSaver.ContextHelper.Calibration;
using TokenSaver.ContextHelper.Configuration;
using TokenSaver.ContextHelper.Pipeline;
using TokenSaver.ContextHelper.Pipeline.Stages;
using TokenSaver.Database.Context;
using TokenSaver.Database.Context.Entities;
using TokenSaver.Library.Configuration;
using TokenSaver.Library.Contracts;
using TokenSaver.Library.Services;
using TokenSaver.MCP.Services;
using TokenSaver.MCP.Tools;

using Xunit;

namespace TokenSaver.MCP.Tests;

/// <summary>
///     Covers the pure/validation branches of LocalModelHelperTools that don't require a live
///     Ollama model: ListOllamaEndpoints (delegates to OllamaEndpointRegistry, already covered
///     at the registry level by OllamaEndpointRegistryTests — this class exercises the
///     string-formatting wrapper), SetAgentPreference (file I/O only), ContextHygiene (DB read
///     only), and RegisterMcpProviders (AiProviderConfigService is a deprecated no-op — no
///     Ollama involved at all).
///
///     CleanAndChat and the Ollama-summarization branch of BuildConciseContext are NOT covered:
///     every reachable line in CleanAndChat calls ContextCleanerService.CleanContextAsync and
///     then unconditionally calls OllamaService.CallModelOnEndpointAsync — there is no
///     input-validation branch (e.g. empty prompt) that returns before the model call, so any
///     test would either require a live model or only exercise the HttpRequestException path
///     thrown deep inside OllamaService, which is already implicitly covered by OllamaService's
///     own error handling and isn't a distinct branch of LocalModelHelperTools itself.
///     BuildConciseContext's gitignore-confirmation branch and workspace/no-files pure path ARE
///     covered below without touching Ollama; the branch that renders a model-adapted response
///     (PromptAssessor.AssessAsync's Tier-2 fallback, ContextCleanerService's
///     ConversationSummarization/SemanticRelevanceFilter/TokenBudget stages) issues real HTTP
///     calls to an intentionally-unreachable endpoint that are caught internally and degrade
///     gracefully (see ConversationSummarizationStage/SemanticRelevanceFilterStage/PromptAssessor
///     doc comments) — so BuildConciseContext_NoRequestedFiles_ReturnsAdaptedContext below
///     exercises that full path end-to-end without a live model, using the same "unreachable
///     HttpClient" pattern as TokenEconomyToolsTests/WorkspaceIndexServiceTests.
/// </summary>
public sealed class LocalModelHelperToolsTests : IDisposable
{
    private readonly string _root;
    private readonly List<IDisposable> _disposables = [];
    private readonly List<HttpClient> _httpClients = [];

    public LocalModelHelperToolsTests()
    {
        _root = Directory.CreateTempSubdirectory("lmhtTests-").FullName;
    }

    public void Dispose()
    {
        foreach (IDisposable d in _disposables)
        {
            d.Dispose();
        }

        foreach (HttpClient h in _httpClients)
        {
            h.Dispose();
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private HttpClient UnreachableHttpClient()
    {
        HttpClient client = new() { BaseAddress = new Uri("http://127.0.0.1:1") };
        _httpClients.Add(client);
        return client;
    }

    private LocalModelHelperTools CreateTools(
        TokenSaverConfig? tokenSaverConfig = null,
        OllamaEndpointRegistry? endpoints = null,
        CallerInfo? callerInfo = null)
    {
        TokenSaverConfig config = tokenSaverConfig ?? new TokenSaverConfig { WorkspaceRoot = _root };
        IOptions<TokenSaverConfig> configOptions = Options.Create(config);

        OllamaService ollama = new(UnreachableHttpClient(), configOptions, NullLogger<OllamaService>.Instance);

        ContextHelperConfig contextHelperConfig = new();
        IOptions<ContextHelperConfig> contextHelperOptions = Options.Create(contextHelperConfig);

        RetrievalCalibrationService calibration = new(UnreachableHttpClient(), contextHelperOptions, NullLogger<RetrievalCalibrationService>.Instance);

        PromptAssessor assessor = new(UnreachableHttpClient(), contextHelperOptions, NullLogger<PromptAssessor>.Instance);

        DeduplicationStage stage01 = new(contextHelperOptions, NullLogger<DeduplicationStage>.Instance);
        LogStrippingStage stage02 = new(contextHelperOptions, NullLogger<LogStrippingStage>.Instance);
        CodeBlockCollapsingStage stage03 = new(contextHelperOptions, NullLogger<CodeBlockCollapsingStage>.Instance);
        ConversationSummarizationStage stage04 = new(UnreachableHttpClient(), contextHelperOptions, NullLogger<ConversationSummarizationStage>.Instance);
        ToolCallCompressionStage stage05 = new(NullLogger<ToolCallCompressionStage>.Instance);
        SemanticRelevanceFilterStage stage06 = new(UnreachableHttpClient(), contextHelperOptions, NullLogger<SemanticRelevanceFilterStage>.Instance, calibration);
        TokenBudgetStage stage07 = new(UnreachableHttpClient(), contextHelperOptions, NullLogger<TokenBudgetStage>.Instance);
        ChunkingStage stage08 = new(contextHelperOptions, NullLogger<ChunkingStage>.Instance);
        ConversationalGarbageStage stage09 = new(NullLogger<ConversationalGarbageStage>.Instance);
        FormattingNormalizationStage stage10 = new(NullLogger<FormattingNormalizationStage>.Instance);
        StrippingHealthAnalyzer healthAnalyzer = new(contextHelperOptions, NullLogger<StrippingHealthAnalyzer>.Instance);

        ContextCleanerService cleaner = new(
            assessor, healthAnalyzer, contextHelperOptions, NullLogger<ContextCleanerService>.Instance,
            stage01, stage02, stage03, stage04, stage05, stage06, stage07, stage08, stage09, stage10);

        WorkspaceConfigManager workspaceConfig = new(configOptions, NullLogger<WorkspaceConfigManager>.Instance);

        WorkspaceIndexDbAccessor accessor = new(NullLogger<WorkspaceIndexDbAccessor>.Instance);
        _disposables.Add(accessor);

        GitStalenessService gitStaleness = new(
            accessor, new GitStalenessChecker(NullLogger<GitStalenessChecker>.Instance), NullLogger<GitStalenessService>.Instance);

        FolderTreeManager folderTree = new(accessor, workspaceConfig, NullLogger<FolderTreeManager>.Instance);
        AiSummarizationService aiSummarization = new(NullLogger<AiSummarizationService>.Instance);
        DraftModelCapacityClient capacityClient = new(UnreachableHttpClient(), NullLogger<DraftModelCapacityClient>.Instance);

        WorkspaceIndexService orchestrator = new(
            Options.Create(new CodeHelperConfig()), [], accessor, gitStaleness, folderTree,
            aiSummarization, capacityClient, NullLogger<WorkspaceIndexService>.Instance);

        ModelRouteResolver modelRoutes = new(configOptions);
        TokenEconomyDbAccessor tokenEconomyDb = new();
        ISavingsTracker savings = new SavingsTracker(tokenEconomyDb, configOptions);
        IRepoIndexer repoIndexer = new RepoIndexer(
            new ChunkExtractor(), ollama, modelRoutes, tokenEconomyDb, configOptions);
        SemanticIndexBackgroundTrigger semanticIndexTrigger = new(
            repoIndexer, tokenEconomyDb, NullLogger<SemanticIndexBackgroundTrigger>.Instance);

        ContextPipelineService contextPipeline = new(
            accessor, cleaner, gitStaleness, workspaceConfig, orchestrator, semanticIndexTrigger,
            savings, calibration, new BuildArtifactDetector(NullLogger<BuildArtifactDetector>.Instance),
            new BehaviorConfigService(NullLogger<BehaviorConfigService>.Instance), configOptions);

        AiProviderConfigService aiProviders = new();

        FakeCallerDetectionService detection = new(callerInfo ?? new CallerInfo { Source = DetectionSource.Default });

        AgentAdapterRegistry registry = new([], NullLogger<AgentAdapterRegistry>.Instance);

        DefaultHttpContext httpContext = new();
        FakeHttpContextAccessor httpContextAccessor = new(httpContext);

        McpContextService mcpContext = new();
        DeferredNotificationService deferred = new();

        OllamaEndpointRegistry endpointRegistry = endpoints ?? new OllamaEndpointRegistry(configOptions, NullLogger<OllamaEndpointRegistry>.Instance);

        BehaviorConfigService behaviorConfig = new(NullLogger<BehaviorConfigService>.Instance);
        _disposables.Add(behaviorConfig);

        LazyContextResolver lazyContext = new(detection, behaviorConfig);

        BehaviorConfigOverlay behaviorConfigOverlay = new(
            behaviorConfig, Options.Create(new CodeHelperConfig()), configOptions, contextHelperOptions);

        InstructionOptimizerService instructionOptimizer = new(
            behaviorConfig, new InstructionCacheService(NullLogger<InstructionCacheService>.Instance),
            NullLogger<InstructionOptimizerService>.Instance);

        WorkspaceRootSessionResolver workspaceRootResolver = new(
            configOptions, contextHelperOptions, orchestrator, contextPipeline, ollama,
            behaviorConfigOverlay, instructionOptimizer, NullLogger<WorkspaceRootSessionResolver>.Instance)
        {
            SkipBackgroundWarmupForTests = true
        };

        return new LocalModelHelperTools(
            ollama, cleaner, workspaceConfig, contextPipeline, aiProviders, configOptions,
            detection, registry, httpContextAccessor, assessor, mcpContext, deferred,
            endpointRegistry, behaviorConfig, lazyContext, workspaceRootResolver);
    }

    // ── ListOllamaEndpoints ───────────────────────────────────────────────────

    [Fact]
    public void ListOllamaEndpoints_ZeroConfig_ReportsSingleDefaultEndpoint()
    {
        LocalModelHelperTools tools = CreateTools();

        string result = tools.ListOllamaEndpoints();

        Assert.Contains("1 Ollama endpoint(s) configured", result, StringComparison.Ordinal);
        Assert.Contains("default", result, StringComparison.Ordinal);
        Assert.Contains("(primary)", result, StringComparison.Ordinal);
        Assert.Contains("healthy", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ListOllamaEndpoints_TwoConfiguredEndpoints_ListsBothWithDetails()
    {
        TokenSaverConfig config = new()
        {
            WorkspaceRoot = _root,
            Endpoints =
            [
                new OllamaEndpointConfig { Name = "primary", Url = "http://localhost:11435", Primary = true },
                new OllamaEndpointConfig { Name = "backup", Url = "http://localhost:11436", Primary = false }
            ]
        };
        OllamaEndpointRegistry registry = new(Options.Create(config), NullLogger<OllamaEndpointRegistry>.Instance);
        registry.RecordFailure("backup");

        LocalModelHelperTools tools = CreateTools(config, registry);

        string result = tools.ListOllamaEndpoints();

        Assert.Contains("2 Ollama endpoint(s) configured", result, StringComparison.Ordinal);
        Assert.Contains("primary (primary)", result, StringComparison.Ordinal);
        Assert.Contains("backup", result, StringComparison.Ordinal);
        Assert.Contains("unhealthy", result, StringComparison.Ordinal);
        Assert.Contains("success/fail: 0/1", result, StringComparison.Ordinal);
    }

    // ── SetAgentPreference ────────────────────────────────────────────────────

    [Fact]
    public async Task SetAgentPreference_ValidInput_WritesPreferencesFileAndReportsValues()
    {
        LocalModelHelperTools tools = CreateTools();

        string result = await tools.SetAgentPreference(
            "Copilot", preferFullFileContent: true, maxFileSizeTokens: 4000, splitLargeFiles: true, workspaceRoot: _root);

        Assert.Contains("Saved preferences for Copilot", result, StringComparison.Ordinal);
        Assert.Contains("preferFullFileContent = True", result, StringComparison.Ordinal);
        Assert.Contains("maxFileSizeTokens     = 4000", result, StringComparison.Ordinal);
        Assert.Contains("splitLargeFiles       = True", result, StringComparison.Ordinal);

        string prefsPath = Path.Combine(_root, ".tokensaver", "preferences.json");
        Assert.True(File.Exists(prefsPath));
        Assert.Contains("Copilot", await File.ReadAllTextAsync(prefsPath));
    }

    [Fact]
    public async Task SetAgentPreference_NoOptionalParams_SavesEmptyPreferencesWithoutThrowing()
    {
        LocalModelHelperTools tools = CreateTools();

        string result = await tools.SetAgentPreference("Continue", workspaceRoot: _root);

        Assert.Contains("Saved preferences for Continue", result, StringComparison.Ordinal);
        Assert.DoesNotContain("preferFullFileContent", result, StringComparison.Ordinal);
        Assert.DoesNotContain("maxFileSizeTokens", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetAgentPreference_CalledTwice_SecondCallMergesRatherThanOverwritingUnrelatedFields()
    {
        LocalModelHelperTools tools = CreateTools();

        _ = await tools.SetAgentPreference("Cursor", preferFullFileContent: true, workspaceRoot: _root);
        string result = await tools.SetAgentPreference("Cursor", maxFileSizeTokens: 8000, workspaceRoot: _root);

        Assert.Contains("preferFullFileContent = True", result, StringComparison.Ordinal);
        Assert.Contains("maxFileSizeTokens     = 8000", result, StringComparison.Ordinal);
    }

    // ── ContextHygiene ────────────────────────────────────────────────────────

    [Fact]
    public async Task ContextHygiene_EmptyWorkspace_ReportsNoStaleSections()
    {
        LocalModelHelperTools tools = CreateTools();

        string result = await tools.ContextHygiene(workspaceRoot: _root);

        Assert.Contains("Context cache: 0 files, 0 sections.", result, StringComparison.Ordinal);
        Assert.Contains("No stale sections found.", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContextHygiene_WithStaleFileRowInIndex_ReportsStaleSection()
    {
        WorkspaceIndexDbAccessor accessor = new(NullLogger<WorkspaceIndexDbAccessor>.Instance);
        _disposables.Add(accessor);
        WorkspaceIndexContext ctx = await accessor.GetDbAsync(_root, CancellationToken.None);
        long staleTimestamp = DateTimeOffset.UtcNow.AddDays(-60).ToUnixTimeMilliseconds();
        FolderEntity folder = new()
        {
            RelativePath = ".",
            ParentFolderId = null,
            Description = "root",
            LastModified = staleTimestamp,
            UpdatedAt = staleTimestamp
        };
        _ = ctx.Folders.Add(folder);
        _ = await ctx.SaveChangesAsync();
        _ = ctx.Files.Add(new FileEntity
        {
            RelativePath = "old.cs",
            FolderId = folder.Id,
            FileType = "cs",
            Description = "stale file",
            ContentHash = "x",
            Size = 10,
            LastModified = staleTimestamp,
            UpdatedAt = staleTimestamp
        });
        _ = await ctx.SaveChangesAsync();

        LocalModelHelperTools tools = CreateTools();

        string result = await tools.ContextHygiene(workspaceRoot: _root);

        Assert.Contains("Context cache: 1 files, 0 sections.", result, StringComparison.Ordinal);
        Assert.Contains("Stale sections (not accessed in 30+ days): 1", result, StringComparison.Ordinal);
        Assert.Contains("old.cs", result, StringComparison.Ordinal);
    }

    // ── RegisterMcpProviders ──────────────────────────────────────────────────

    [Fact]
    public async Task RegisterMcpProviders_AutoRegistrationDisabled_ReturnsExplanatoryNoOpMessage()
    {
        // AiProviderConfigService.UpdateAllProvidersAsync is a permanent no-op (see its doc
        // comment) — auto-write to AI client config files was removed entirely, so this always
        // takes the "results.Count == 0" branch regardless of config. No .mcp.json or other file
        // is touched.
        LocalModelHelperTools tools = CreateTools();

        string result = await tools.RegisterMcpProviders();

        Assert.Contains("Auto-registration is disabled", result, StringComparison.Ordinal);
        Assert.Contains("Open Install Prompt", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisterMcpProviders_ExplicitPortOverride_DoesNotThrow()
    {
        LocalModelHelperTools tools = CreateTools();

        string result = await tools.RegisterMcpProviders(port: 55555);

        Assert.Contains("Auto-registration is disabled", result, StringComparison.Ordinal);
    }

    // ── BuildConciseContext ───────────────────────────────────────────────────

    [Fact]
    public async Task BuildConciseContext_RequestedGitignoredFile_ReturnsConfirmationPromptInsteadOfBuilding()
    {
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "secret.txt\n");
        File.WriteAllText(Path.Combine(_root, "secret.txt"), "sensitive");

        LocalModelHelperTools tools = CreateTools();

        string result = await tools.BuildConciseContext(
            "look at this file", requestedFiles: ["secret.txt"], workspaceRoot: _root);

        Assert.Contains("listed in .gitignore", result, StringComparison.Ordinal);
        Assert.Contains("secret.txt", result, StringComparison.Ordinal);
        Assert.Contains("includeIgnored:", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildConciseContext_BareFilenameNotFoundAnywhere_AddsSearchNoteAndStillBuilds()
    {
        LocalModelHelperTools tools = CreateTools();

        string result = await tools.BuildConciseContext(
            "look at this file", requestedFiles: ["does-not-exist.md"], workspaceRoot: _root);

        Assert.Contains("was not found anywhere in the workspace", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildConciseContext_NoRequestedFiles_ReturnsAdaptedContextWithMetadataHeader()
    {
        // Exercises the full non-Ollama-model path: prompt assessment (Tier-1 keyword match,
        // confidence >= threshold so Tier-2 model call is skipped for this prompt), the cleaner
        // pipeline (stages 01-10, all pure/regex-based except summarization/semantic-filter/
        // token-budget stages which hit an intentionally-unreachable HttpClient and degrade
        // gracefully per their own doc comments), workspace indexing (IndexWorkspaceAsync over
        // an empty tree), and DefaultAgentAdapter's context rendering. No live Ollama call is
        // reachable from this branch.
        LocalModelHelperTools tools = CreateTools();

        string result = await tools.BuildConciseContext("refactor the login module", workspaceRoot: _root);

        Assert.Contains("<!-- context:", result, StringComparison.Ordinal);
        Assert.Contains("files indexed", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildConciseContext_ConfirmedIgnoredFile_IncludesItInsteadOfPromptingAgain()
    {
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "secret.txt\n");
        File.WriteAllText(Path.Combine(_root, "secret.txt"), "sensitive");

        LocalModelHelperTools tools = CreateTools();

        string result = await tools.BuildConciseContext(
            "look at this file", requestedFiles: ["secret.txt"], workspaceRoot: _root,
            includeIgnored: ["secret.txt"]);

        Assert.DoesNotContain("listed in .gitignore", result, StringComparison.Ordinal);
        Assert.Contains("<!-- context:", result, StringComparison.Ordinal);
    }

    // ── CleanAndChat ──────────────────────────────────────────────────────────
    // No test: every reachable statement in CleanAndChat calls ContextCleanerService then
    // unconditionally calls OllamaService.CallModelOnEndpointAsync — there is no argument
    // validation or early-return branch before the model call (empty prompt is passed straight
    // through to the pipeline and then to Ollama). Testing it would either require a live model
    // to assert on the returned response text, or would only prove that OllamaService surfaces
    // an HttpRequestException from an unreachable endpoint, which is already OllamaService's own
    // concern, not a distinct behavior of LocalModelHelperTools.CleanAndChat.

    private sealed class FakeCallerDetectionService(CallerInfo toReturn) : ICallerDetectionService
    {
        public CallerInfo Detect(HttpContext httpContext, string? mcpClientName = null, string? mcpClientVersion = null)
        {
            return toReturn;
        }
    }

    private sealed class FakeHttpContextAccessor(HttpContext? context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = context;
    }
}
