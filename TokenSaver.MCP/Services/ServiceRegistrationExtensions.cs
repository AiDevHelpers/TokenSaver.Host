using TokenSaver.ContextHelper.Calibration;
using TokenSaver.ContextHelper.Configuration;
using TokenSaver.ContextHelper.Pipeline;
using TokenSaver.ContextHelper.Pipeline.Stages;
using TokenSaver.CodeHelper.AutoWorkflow;
using TokenSaver.CodeHelper.Calibration;
using TokenSaver.CodeHelper.Configuration;
using TokenSaver.CodeHelper.Indexing;
using TokenSaver.CodeHelper.Patching;
using TokenSaver.CodeHelper.Planning;
using TokenSaver.CodeHelper.Research;
using TokenSaver.CodeHelper.Contracts;
using TokenSaver.Config.Contracts;
using TokenSaver.Common.Core.Configuration;
using TokenSaver.Common.Core.Contracts;
using TokenSaver.Common.Contracts;
using TokenSaver.Common.Models;
using TokenSaver.Config;
using TokenSaver.Database.Context;
using TokenSaver.OllamaIntegration.Services;
using TokenSaver.TokenEconomy.Contracts;
using TokenSaver.TokenEconomy.Services;
using TokenSaver.Utilities.Services;
using TokenSaver.WslControl;
using TokenSaver.WslControl.Contracts;
using TokenSaver.LanguageHelpers.CSharp.Extensions;
using TokenSaver.LanguageHelpers.PowerShell.Extensions;
using TokenSaver.LanguageHelpers.Python.Extensions;
using TokenSaver.AgentHelpers.Anthropic;
using TokenSaver.AgentHelpers.Core.Extensions;
using TokenSaver.AgentHelpers.DeepSeek;
using TokenSaver.AgentHelpers.OpenSource;
using TokenSaver.AgentHelpers.Qwen;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;

namespace TokenSaver.MCP.Services;

/// <summary>
/// Extension methods for registering TokenSaver MCP services.
/// Organized by functional area to keep Program.cs clean and maintainable.
/// </summary>
internal static class ServiceRegistrationExtensions
{
    /// <summary>Register all context pipeline stages and related services.</summary>
    public static IServiceCollection AddContextPipeline(this IServiceCollection services)
    {
        services.AddSingleton<DeduplicationStage>();
        services.AddSingleton<LogStrippingStage>();
        services.AddSingleton<CodeBlockCollapsingStage>();
        services.AddSingleton<ConversationSummarizationStage>();
        services.AddSingleton<ToolCallCompressionStage>();
        services.AddSingleton<ChunkingStage>();
        services.AddSingleton<RetrievalCalibrationService>();
        services.AddSingleton<SemanticRelevanceFilterStage>();
        services.AddSingleton<TokenBudgetStage>();
        services.AddSingleton<ConversationalGarbageStage>();
        services.AddSingleton<FormattingNormalizationStage>();
        services.AddSingleton<PromptAssessor>();
        services.AddSingleton<StrippingHealthAnalyzer>();
        services.AddSingleton<ContextCleanerService>();
        return services;
    }

    /// <summary>Register CodeHelper services (research, symbol parsing, planning, patching).</summary>
    public static IServiceCollection AddCodeHelperServices(this IServiceCollection services)
    {
        services.AddSingleton<ResearchContextService>();
        services.AddSingleton<ISymbolParser, CSharpSymbolParser>();
        services.AddSingleton<ISymbolParser, TypeScriptSymbolParser>();
        services.AddSingleton<ISymbolParser, GenericSymbolParser>();
        services.AddSingleton<PatchEngine>();
        services.AddSingleton<PlanStore>();
        services.AddSingleton<PlanExecutor>();
        services.AddSingleton<CalibrationService>();
        services.AddSingleton<CapabilityAssessor>();
        services.AddSingleton<WorkflowSessionStore>();
        services.AddSingleton<CodeWorkflowService>();
        services.AddSingleton<SimpleModelServerInterceptorService>();
        services.AddSingleton<TaskClassifier>();
        services.AddSingleton<PlanBuilder>();
        return services;
    }

    /// <summary>Register workspace indexing and database services.</summary>
    public static IServiceCollection AddIndexingServices(this IServiceCollection services)
    {
        services.AddSingleton<GitStalenessChecker>();
        services.AddSingleton<SimpleModelServerCapacityClient>();
        services.AddSingleton<WorkspaceIndexDbAccessor>();
        services.AddSingleton<BehaviorConfigService>();
        services.AddSingleton<IBehaviorConfigService>(sp => sp.GetRequiredService<BehaviorConfigService>());
        services.AddSingleton<BehaviorConfigOverlay>();
        services.AddSingleton<WorkspaceConfigManager>();
        services.AddSingleton<IWorkspaceConfigManager>(sp => sp.GetRequiredService<WorkspaceConfigManager>());
        services.AddSingleton<GitStalenessService>();
        services.AddSingleton<FolderTreeManager>();
        services.AddSingleton<SymbolSearchService>();
        services.AddSingleton<AiSummarizationService>();
        services.AddSingleton<DependencyTrackingService>();
        services.AddSingleton<WorkspaceIndexService>();
        services.AddSingleton<IWorkspaceIndexService>(sp => sp.GetRequiredService<WorkspaceIndexService>());
        services.AddSingleton<BuildArtifactDetector>();
        services.AddSingleton<ContextPipelineService>();
        services.AddSingleton<IContextPipelineService>(sp => sp.GetRequiredService<ContextPipelineService>());
        services.AddSingleton<IFileReindexer>(sp => sp.GetRequiredService<WorkspaceIndexService>());
        return services;
    }

    /// <summary>Register MCP context, deferred notifications, and configuration services.</summary>
    public static IServiceCollection AddMcpCoreServices(this IServiceCollection services)
    {
        services.AddSingleton<McpContextService>();
        services.AddSingleton<DeferredNotificationService>();
        services.AddSingleton<ICodeSummarizer, OllamaCodeSummarizer>();
        services.AddSingleton<AiProviderConfigService>();
        services.AddSingleton<WorkspaceCodeScanner>();
        services.AddSingleton<AccuracyTestService>();
        services.AddSingleton<ITestRunner, TestRunnerService>();
        services.AddHostedService<ProviderAutoRegistrationService>();
        return services;
    }

    /// <summary>Register token economy and distillation services.</summary>
    public static IServiceCollection AddTokenEconomyServices(this IServiceCollection services)
    {
        services.AddSingleton<TokenEconomyDbAccessor>();
        services.AddSingleton<ModelRouteResolver>();
        services.AddSingleton<ISavingsTracker, SavingsTracker>();
        services.AddSingleton<IDistillationService, DistillationService>();
        services.AddSingleton<RawArtifactWriter>();
        services.AddSingleton<IChunkExtractor, ChunkExtractor>();
        services.AddSingleton<IRepoIndexer, RepoIndexer>();
        services.AddSingleton<IndexSelfTest>();
        services.AddSingleton<ISemanticSearchService, SemanticSearchService>();
        services.AddSingleton<IRerankService, RerankService>();
        services.AddSingleton<IConditionalRetrievalService, ConditionalRetrievalService>();
        services.AddSingleton<SemanticIndexBackgroundTrigger>();
        services.AddSingleton<SymbolScanBackgroundTrigger>();
        return services;
    }

    /// <summary>Register HTTP clients for Ollama endpoint communication.</summary>
    /// <remarks>
    ///     The User-Agent header is applied globally via <c>ConfigureHttpClientDefaults</c> in
    ///     Program.cs (so background probe clients created through
    ///     <see cref="IHttpClientFactory.CreateClient()" /> are attributed too), not per-client here.
    /// </remarks>
    public static IServiceCollection AddOllamaHttpClients(this IServiceCollection services, TokenSaverConfig config)
    {
        services.AddSingleton<OllamaEndpointRegistry>();
        services.AddTransient<EndpointRoutingHandler>();
        services.AddHostedService<EndpointHealthMonitor>();
        services.AddHostedService<GitignoreWatcherService>();
        services.AddHostedService<McpRegistrationCheckService>();

        // Ollama service client (5-minute timeout for long-running operations)
        services.AddHttpClient<OllamaService>(client =>
        {
            client.BaseAddress = new Uri(config.OllamaUrl);
            client.Timeout = TimeSpan.FromMinutes(5);
        }).AddHttpMessageHandler<EndpointRoutingHandler>();

        // SimpleModelServer capacity check client (10-second timeout, quick checks only)
        services.AddHttpClient<SimpleModelServerCapacityClient>(client =>
        {
            client.BaseAddress = new Uri(config.OllamaUrl);
            client.Timeout = TimeSpan.FromSeconds(10);
        }).AddHttpMessageHandler<EndpointRoutingHandler>();

        // SimpleModelServer admin config client (GET/POST /admin/config on the router)
        services.AddHttpClient<AgentConfigService>(client =>
        {
            client.BaseAddress = new Uri(config.OllamaUrl);
            client.Timeout = TimeSpan.FromSeconds(10);
        }).AddHttpMessageHandler<EndpointRoutingHandler>();

        return services;
    }

    /// <summary>Register HTTP clients for pipeline stages that call the model.</summary>
    /// <remarks>The User-Agent header is applied globally — see <see cref="AddOllamaHttpClients" />.</remarks>
    public static IServiceCollection AddPipelineHttpClients(this IServiceCollection services, TokenSaverConfig config)
    {
        const string PassthroughAgent = "passthrough";  // Tells SimpleModelServer router not to switch agents

        // PromptAssessor: quick model evaluation (30 second timeout)
        services.AddHttpClient<PromptAssessor>(client =>
        {
            client.BaseAddress = new Uri(config.OllamaUrl);
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Add("X-Agent-Type", PassthroughAgent);
        }).AddHttpMessageHandler<EndpointRoutingHandler>();

        // Conversation Summarization: longer text processing (2 minute timeout)
        services.AddHttpClient<ConversationSummarizationStage>(client =>
        {
            client.BaseAddress = new Uri(config.OllamaUrl);
            client.Timeout = TimeSpan.FromMinutes(2);
            client.DefaultRequestHeaders.Add("X-Agent-Type", PassthroughAgent);
        }).AddHttpMessageHandler<EndpointRoutingHandler>();

        // Semantic Relevance Filter: text analysis (2 minute timeout)
        services.AddHttpClient<SemanticRelevanceFilterStage>(client =>
        {
            client.BaseAddress = new Uri(config.OllamaUrl);
            client.Timeout = TimeSpan.FromMinutes(2);
            client.DefaultRequestHeaders.Add("X-Agent-Type", PassthroughAgent);
        }).AddHttpMessageHandler<EndpointRoutingHandler>();

        // Retrieval Calibration: scoring operations (30 second timeout)
        services.AddHttpClient<RetrievalCalibrationService>(client =>
        {
            client.BaseAddress = new Uri(config.OllamaUrl);
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Add("X-Agent-Type", PassthroughAgent);
        }).AddHttpMessageHandler<EndpointRoutingHandler>();

        // Token Budget: quick estimation (2 minute timeout)
        services.AddHttpClient<TokenBudgetStage>(client =>
        {
            client.BaseAddress = new Uri(config.OllamaUrl);
            client.Timeout = TimeSpan.FromMinutes(2);
            client.DefaultRequestHeaders.Add("X-Agent-Type", PassthroughAgent);
        }).AddHttpMessageHandler<EndpointRoutingHandler>();

        // Task Classifier: CodeHelper classification service (30 second timeout)
        services.AddHttpClient<TaskClassifier>(client =>
        {
            client.BaseAddress = new Uri(config.OllamaUrl);
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Add("X-Agent-Type", PassthroughAgent);
        }).AddHttpMessageHandler<EndpointRoutingHandler>();

        return services;
    }

    /// <summary>Register AgentHelpers adapters for different AI clients.</summary>
    public static IServiceCollection AddAgentHelpersAdapters(this IServiceCollection services, string ollamaModel)
    {
        services.AddSingleton<AgentConfigResolver>();
        services.AddSingleton<LazyContextResolver>();
        services.AddSingleton<InstructionCacheService>();
        services.AddSingleton<InstructionOptimizerService>();
        services.AddSingleton(_ => new InstructionStoreService(InstructionStoreService.DefaultRoot));
        services.AddSingleton<IPromptAdapter, AgentHelpersPromptAdapter>();

        // Register AgentHelpers with client-specific adapters (priority 90) before
        // model-family adapters (priority 60–80) so ClientName detection wins over model hints
        services
            .AddAgentHelpers(ollamaModel)
            .AddAdapter<CopilotClientAdapter>()
            .AddAdapter<ContinueClientAdapter>()
            .AddAdapter<CursorClientAdapter>()
            .AddAdapter<AnthropicAgentAdapter>()
            .AddAdapter<QwenAgentAdapter>()
            .AddAdapter<DeepSeekAgentAdapter>()
            .AddAdapter<OpenSourceAgentAdapter>();

        return services;
    }

    /// <summary>Register configuration sections used by various services.</summary>
    public static IServiceCollection AddConfigurationSections(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ContextHelperConfig>(
            configuration.GetSection(TokenSaverConfig.SectionName).GetSection("ContextHelper"));

        services.Configure<CodeHelperConfig>(
            configuration.GetSection(TokenSaverConfig.SectionName).GetSection("CodeHelper"));

        return services;
    }

    /// <summary>Configure workspace resolution and session management.</summary>
    public static IServiceCollection AddWorkspaceSessionServices(this IServiceCollection services)
    {
        services.AddSingleton<WorkspaceRootSessionResolver>();
        services.AddSingleton<UnhandledFaultTracker>();
        return services;
    }

    /// <summary>Register debugger control service for VS Code and Visual Studio.</summary>
    public static IServiceCollection AddDebuggerServices(this IServiceCollection services)
    {
        services.AddSingleton<IDebuggerControl, DebuggerControlService>();
        services.AddSingleton<IDebuggerApiClient, DebuggerApiImplementation>();
        services.AddSingleton<IDebugOutputCapture, DebugOutputCaptureService>();
        services.AddSingleton<IIDELauncher, IDELauncherService>();

        // Phase 1: Foundation — Structured event capture, problem tracking, and client tracking
        services.AddSingleton<DebuggerEventService>();
        services.AddSingleton<IDebuggerEventCapture>(sp => sp.GetRequiredService<DebuggerEventService>());
        services.AddSingleton<DebuggerProblemService>();
        services.AddSingleton<IDebuggerProblemCapture>(sp => sp.GetRequiredService<DebuggerProblemService>());
        services.AddSingleton<IClientDebugContextService, ClientDebugContextService>();

        // Phase 2: Streaming endpoints and real-time event distribution
        services.AddSingleton<DebuggerEventStreamManager>(sp =>
            new DebuggerEventStreamManager(
                sp.GetRequiredService<ILogger<DebuggerEventStreamManager>>(),
                sp.GetRequiredService<IDebuggerEventCapture>()));

        // Phase 3: Persistence and historical analysis
        services.AddHostedService<DebuggerEventLogWriter>(sp =>
            new DebuggerEventLogWriter(
                sp.GetRequiredService<ILogger<DebuggerEventLogWriter>>(),
                sp.GetRequiredService<IDebuggerEventCapture>()));

        services.AddHostedService<DebuggerProblemLogWriter>(sp =>
            new DebuggerProblemLogWriter(
                sp.GetRequiredService<ILogger<DebuggerProblemLogWriter>>(),
                sp.GetRequiredService<IDebuggerProblemCapture>()));

        services.AddSingleton<DebuggerEventQueryService>();
        services.AddSingleton<DebuggerProblemQueryService>();

        // Phase 4: Multi-client extension integration
        services.AddSingleton<ExtensionRegistryService>();

        return services;
    }

    /// <summary>
    ///     Registers the WSL provider stack (TokenSaver.WslControl) plus the host-side adapter that
    ///     binds its <see cref="IWslCommandModel" /> seam to the local model (OllamaService), so the
    ///     natural-language planner can route command synthesis through the SimpleModelServer/Ollama router.
    /// </summary>
    public static IServiceCollection AddWslControlServices(this IServiceCollection services)
    {
        // Bind the WslControl command-model seam to the local model before AddWslControl runs,
        // so the planner resolves this implementation.
        services.AddSingleton<IWslCommandModel, OllamaWslCommandModel>();
        services.AddWslControl();
        return services;
    }

    /// <summary>
    ///     Registers the per-language code-quality helpers (TokenSaver.LanguageHelpers.*) and their shared
    ///     Core services (process runner + <c>ILanguageHelperRegistry</c>). Each <c>Add*LanguageHelper</c>
    ///     adds the Core layer idempotently, so the registry aggregates all three helpers.
    /// </summary>
    public static IServiceCollection AddLanguageHelperServices(this IServiceCollection services)
    {
        services.AddCSharpLanguageHelper();
        services.AddPowerShellLanguageHelper();
        services.AddPythonLanguageHelper();
        return services;
    }
}
