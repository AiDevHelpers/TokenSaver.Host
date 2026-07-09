using TokenSaver.AgentHelpers.Anthropic;
using TokenSaver.AgentHelpers.Core.Extensions;
using TokenSaver.AgentHelpers.DeepSeek;
using TokenSaver.AgentHelpers.OpenSource;
using TokenSaver.AgentHelpers.Qwen;

using TokenSaver.CodeHelper.AutoWorkflow;
using TokenSaver.CodeHelper.Calibration;
using TokenSaver.CodeHelper.Configuration;
using TokenSaver.CodeHelper.Indexing;
using TokenSaver.CodeHelper.Models.Index;
using TokenSaver.CodeHelper.Patching;
using TokenSaver.CodeHelper.Planning;
using TokenSaver.CodeHelper.Research;
using TokenSaver.Config;
using TokenSaver.ContextHelper.Configuration;
using TokenSaver.ContextHelper.Pipeline;
using TokenSaver.ContextHelper.Pipeline.Stages;
using TokenSaver.Docs;
using TokenSaver.Docs.Prompts;
using TokenSaver.Library.Configuration;
using TokenSaver.Library.Contracts;
using TokenSaver.Database.Context;
using TokenSaver.Library.Models.AiProviders;
using TokenSaver.Library.Models.Context;
using TokenSaver.Library.Services;
using TokenSaver.MCP.HealthChecks;
using TokenSaver.MCP.Middleware;
using TokenSaver.MCP.Services;
using TokenSaver.MCP.Tools;

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging.EventLog;
using Microsoft.Extensions.Options;

using System.Collections.ObjectModel;
using System.Diagnostics;

// ── Stdio detection ──
// When Claude Desktop (or any MCP host) launches this server as a command, stdin is a pipe.
// In that mode we add stdio transport alongside HTTP so Claude auto-manages the process
// while other clients (Copilot, Continue, Cursor) still connect via HTTP on the normal port.
// IMPORTANT: stdout is the MCP protocol channel in stdio mode — redirect it to stderr so
// ASP.NET startup banners and any Console.Write calls don't corrupt the stream.
//
// TOKENSAVER_HTTP_ONLY=1 is set by the VS Code extension when it spawns this server.
// The extension uses HTTP-only transport; without this guard, the extension's stdin=NUL
// makes Console.IsInputRedirected=true, the stdio transport gets added, immediately reads
// EOF from stdin, and shuts the server down before the extension can connect via HTTP.
//
// Windows Service (SCM) launch is the same category of false-positive risk: Session 0 has no
// attached console at all, and Console.IsInputRedirected's behavior there isn't something to
// rely on — same bug shape as the VS Code case above (stdio transport added, reads immediate
// EOF, server shuts itself down) just from a different launcher. So this is checked
// explicitly and always forces HTTP-only under the SCM, regardless of what
// Console.IsInputRedirected happens to report in that session.
bool isStdio = Console.IsInputRedirected
              && !WindowsServiceHelpers.IsWindowsService()
              && Environment.GetEnvironmentVariable("TOKENSAVER_HTTP_ONLY") != "1";
if (isStdio)
{
    Console.SetOut(Console.Error);
}

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// ── Windows Service support ──
// A no-op when launched normally (console, or spawned stdio by an MCP host) — only takes
// effect when the SCM launches this exe non-interactively, exactly like DraftModel.Loader's
// use of the same helper. Lets TokenSaver.MCP be installed as an always-on background
// service (HTTP mode, serving Copilot/Continue/Cursor on ServerPort) independent of whether
// Claude Desktop is running its own separate stdio-spawned instance via the .mcpb.
builder.Host.UseWindowsService(o => o.ServiceName = "TokenSaverMCP");

// Microsoft.Extensions.Logging console setup (single-line, yyyy-MM-dd HH:mm:ss timestamps) —
// the same convention DraftModel.Loader/Program.cs uses, so both projects behave consistently.
// Plus an EventLog sink when running as a Windows service: console output is invisible under
// the SCM (no console session for a service), so without this sink every ILogger call vanished
// silently in service mode and the VS Code extension's pollTokenSaverServiceEventLog() (which
// reads ProviderName='TokenSaver' from the Application log) had nothing to poll. See
// EventLogRegistration.TryAddTokenSaverEventLog for the source-registration guard — mirrors
// DraftModel.Loader/Program.cs's EventLogRegistration exactly, just with a different source name.
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});
builder.Logging.SetMinimumLevel(LogLevel.Information);

if (WindowsServiceHelpers.IsWindowsService() && OperatingSystem.IsWindows())
{
    EventLogRegistration.TryAddTokenSaverEventLog(builder.Logging);
}

// ── Configuration ──
builder.Services.Configure<TokenSaverConfig>(
    builder.Configuration.GetSection(TokenSaverConfig.SectionName));

// Legacy prefix from before the LocalHelper -> TokenSaver rename, added first so the current
// "TOKENSAVER_" prefix (added after) wins on any key present under both.
builder.Configuration.AddEnvironmentVariables("LOCALHELPER_");
builder.Configuration.AddEnvironmentVariables("TOKENSAVER_");

// ── HTTP context access (needed by AgentHelpers caller detection) ──
builder.Services.AddHttpContextAccessor();

// ── ContextHelper config ──
builder.Services.Configure<ContextHelperConfig>(
    builder.Configuration.GetSection(TokenSaverConfig.SectionName).GetSection("ContextHelper"));

// ── CodeHelper config ──
builder.Services.Configure<CodeHelperConfig>(
    builder.Configuration.GetSection(TokenSaverConfig.SectionName).GetSection("CodeHelper"));

// ── Context pipeline stages (all stateless singletons) ──
builder.Services.AddSingleton<DeduplicationStage>();
builder.Services.AddSingleton<LogStrippingStage>();
builder.Services.AddSingleton<CodeBlockCollapsingStage>();
builder.Services.AddSingleton<ConversationSummarizationStage>();
builder.Services.AddSingleton<ToolCallCompressionStage>();
builder.Services.AddSingleton<ChunkingStage>();
builder.Services.AddSingleton<SemanticRelevanceFilterStage>();
builder.Services.AddSingleton<TokenBudgetStage>();
builder.Services.AddSingleton<ConversationalGarbageStage>();
builder.Services.AddSingleton<FormattingNormalizationStage>();
builder.Services.AddSingleton<PromptAssessor>();
builder.Services.AddSingleton<StrippingHealthAnalyzer>();
builder.Services.AddSingleton<ContextCleanerService>();

// ── CodeHelper services ──
builder.Services.AddSingleton<ResearchContextService>();
builder.Services.AddSingleton<ISymbolParser, CSharpSymbolParser>();
builder.Services.AddSingleton<ISymbolParser, TypeScriptSymbolParser>();
builder.Services.AddSingleton<ISymbolParser, GenericSymbolParser>();
builder.Services.AddSingleton<PatchEngine>();
builder.Services.AddSingleton<PlanStore>();
builder.Services.AddSingleton<PlanExecutor>();

// ── Calibration & auto-workflow ──
builder.Services.AddSingleton<CalibrationService>();
builder.Services.AddSingleton<CapabilityAssessor>();
builder.Services.AddSingleton<WorkflowSessionStore>();
builder.Services.AddSingleton<CodeWorkflowService>();

// ── Unified index service ──
// Replaces the old two-database pattern (context.db + code-index.db) with a single
// EF-Core-backed index.db. See plans/TokenSaver.UnifiedIndex.plan.md.
builder.Services.AddSingleton<GitStalenessChecker>();
builder.Services.AddSingleton<DraftModelCapacityClient>();
builder.Services.AddSingleton<WorkspaceIndexDbAccessor>();
// Behavior config (.tokensaver/config.json + %ProgramData%\TokenSaver\config.json, local-over-global,
// live-reloaded via FileSystemWatcher). See TokenSaver.Config.
builder.Services.AddSingleton<BehaviorConfigService>();
builder.Services.AddSingleton<BehaviorConfigOverlay>();
builder.Services.AddSingleton<WorkspaceConfigManager>();
builder.Services.AddSingleton<GitStalenessService>();
builder.Services.AddSingleton<FolderTreeManager>();
builder.Services.AddSingleton<SymbolSearchService>();
builder.Services.AddSingleton<AiSummarizationService>();
builder.Services.AddSingleton<DependencyTrackingService>();
builder.Services.AddSingleton<WorkspaceIndexService>();
builder.Services.AddSingleton<ContextPipelineService>();
builder.Services.AddSingleton<IFileReindexer>(sp => sp.GetRequiredService<WorkspaceIndexService>());

// ── Draft model interception ──
// Auto-intercepts code-writing prompts, routes them through the local model first,
// falling back to the calling AI for architecture/design decisions.
builder.Services.AddSingleton<DraftModelInterceptorService>();

// ── MCP context service ──
builder.Services.AddSingleton<McpContextService>();

// ── Deferred notification queue ──
// Background services post here; tools drain it on the next active client call.
builder.Services.AddSingleton<DeferredNotificationService>();

// ── Auto-registration service (disabled) ──
// Formerly waited for DraftModel to warm up, then auto-wrote this MCP server's entry into
// every detected AI provider config. That auto-write is removed for all providers — see
// ProviderAutoRegistrationService and AiProviderConfigService for why (config format
// fragility across tools). Still registered so startup wiring doesn't change; it now no-ops
// immediately. Use the "TokenSaver: Open Install Prompt" VS Code command instead.
builder.Services.AddHostedService<ProviderAutoRegistrationService>();

// ── Services ──
builder.Services.AddSingleton<ICodeSummarizer, OllamaCodeSummarizer>();
builder.Services.AddSingleton<AiProviderConfigService>();
builder.Services.AddSingleton<WorkspaceCodeScanner>(); // now from TokenSaver.CodeHelper.Indexing
builder.Services.AddSingleton<AccuracyTestService>();
builder.Services.AddSingleton<AgentConfigService>();

// ── Token-economy tool surface ──
// distill()-first design: bulk tool output (test/lint/log/diff text) is compressed locally
// before it's returned to the calling AI. TokenEconomyDbAccessor is a separate DB-access seam
// from WorkspaceIndexDbAccessor (see that type's doc comment) even though both open the same
// ".tokensaver/index.db" file.
builder.Services.AddSingleton<TokenEconomyDbAccessor>();
builder.Services.AddSingleton<ModelRouteResolver>();
builder.Services.AddSingleton<ISavingsTracker, SavingsTracker>();
builder.Services.AddSingleton<IDistillationService, DistillationService>();
builder.Services.AddSingleton<RawArtifactWriter>();
builder.Services.AddSingleton<IChunkExtractor, ChunkExtractor>();
builder.Services.AddSingleton<IRepoIndexer, RepoIndexer>();
builder.Services.AddSingleton<ISemanticSearchService, SemanticSearchService>();

// Shared Ollama HTTP client config
TokenSaverConfig ollamaConfig = builder.Configuration
    .GetSection(TokenSaverConfig.SectionName)
    .Get<TokenSaverConfig>() ?? new TokenSaverConfig();

// When DraftModel is installed as a Windows service, read its config from ProgramData
// to auto-discover the router host/port rather than relying on a hardcoded OllamaUrl.
DraftModelConfigResolver.ApplyIfInstalled(ollamaConfig);

// Propagate the resolved OllamaUrl back into IOptions<TokenSaverConfig> so that
// anything injecting the config (startup log, diagnostics, etc.) sees the same URL
// that the HttpClients were registered with.
string resolvedOllamaUrl = ollamaConfig.OllamaUrl;
builder.Services.PostConfigure<TokenSaverConfig>(cfg => cfg.OllamaUrl = resolvedOllamaUrl);

// Also propagate any endpoints synthesized above (e.g. the "draftmodel-loader" entry) into
// the bound IOptions<TokenSaverConfig> that OllamaEndpointRegistry reads — without this,
// the registry would only ever see whatever TokenSaver:Endpoints was in appsettings,
// missing the auto-discovered router endpoint added when DraftModelServiceInstalled is true.
Collection<OllamaEndpointConfig> resolvedEndpoints = ollamaConfig.Endpoints;
builder.Services.PostConfigure<TokenSaverConfig>(cfg => cfg.Endpoints = resolvedEndpoints);

// Auto-detect WorkspaceRoot by walking up to the nearest repo root.
// We always walk up (not just when WorkspaceRoot is unset) because the VS Code extension
// passes its first workspace folder as an env var, which may be a project sub-folder
// (e.g. F:\mcp\TokenSaver\TokenSaver.MCP) rather than the repo root (F:\mcp).
// Starting point: configured/env-var WorkspaceRoot if set, otherwise ContentRootPath.
{
    string searchFrom = !string.IsNullOrWhiteSpace(ollamaConfig.WorkspaceRoot)
        ? ollamaConfig.WorkspaceRoot
        : builder.Environment.ContentRootPath;

    string? resolved = WorkspaceRootResolver.FindRepoRoot(searchFrom);

    // The Windows service is always started by the SCM from C:\Windows\System32 — the walk
    // above finds nothing from there. Fall back to whatever a previous, cwd-aware launch
    // (stdio from inside the repo, or the VS Code extension) persisted to a small dedicated
    // bootstrap file — separate from the user-facing behavior config.json (see
    // WorkspaceRootResolver's doc comment for why it doesn't belong there).
    resolved ??= WorkspaceRootResolver.ReadPersistedWorkspaceRoot();

    if (resolved is not null)
    {
        ollamaConfig.WorkspaceRoot = resolved;
        WorkspaceRootResolver.PersistWorkspaceRoot(resolved);
    }
}

if (!string.IsNullOrWhiteSpace(ollamaConfig.WorkspaceRoot))
{
    _ = builder.Services.PostConfigure<TokenSaverConfig>(cfg => cfg.WorkspaceRoot = ollamaConfig.WorkspaceRoot);
}

// Register prompt adapter before OllamaService so it's available via DI injection
builder.Services.AddSingleton<IPromptAdapter, AgentHelpersPromptAdapter>();

// ── Ollama endpoint registry & routing ──
// OllamaEndpointRegistry holds the configured endpoint(s) (or a single synthetic "default"
// endpoint built from OllamaUrl/OllamaModel when TokenSaver:Endpoints isn't set) plus their
// runtime health. EndpointRoutingHandler is attached to every typed HttpClient below; it
// rewrites each outgoing request's scheme/host/port to whatever endpoint the registry selects
// for that call (honoring failover/round-robin/explicit strategy), so all 7 consumers route
// through the registry per-request instead of being pinned to one URL at registration time.
// Each client's own BaseAddress below is only a fallback path source for building relative
// URIs before the handler rewrites the authority — normal operation always goes through the
// registry's selected endpoint.
builder.Services.AddSingleton<OllamaEndpointRegistry>();
builder.Services.AddTransient<EndpointRoutingHandler>();
builder.Services.AddHostedService<EndpointHealthMonitor>();
builder.Services.AddHostedService<GitignoreWatcherService>();
builder.Services.AddHostedService<McpRegistrationCheckService>();

builder.Services.AddHttpClient<OllamaService>(client =>
{
    client.BaseAddress = new Uri(ollamaConfig.OllamaUrl);
    client.Timeout = TimeSpan.FromMinutes(5);
}).AddHttpMessageHandler<EndpointRoutingHandler>();

// DraftModelCapacityClient shares the same HttpClient named after OllamaService's typed client.
// We register a separate named client for it because its timeout is shorter (just a quick GET).
builder.Services.AddHttpClient<DraftModelCapacityClient>(client =>
{
    client.BaseAddress = new Uri(ollamaConfig.OllamaUrl);
    client.Timeout = TimeSpan.FromSeconds(10);
}).AddHttpMessageHandler<EndpointRoutingHandler>();

// Pipeline stages that make HTTP calls get their own named HttpClient.
// X-Agent-Type: passthrough tells the DraftModel router to use the currently loaded
// agent without switching — these internal calls contain agent-keyword vocabulary
// (e.g. "Planning" in PromptAssessor's category list) that would otherwise cause
// spurious agent reloads on every pipeline invocation.
builder.Services.AddHttpClient<PromptAssessor>(client =>
{
    client.BaseAddress = new Uri(ollamaConfig.OllamaUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.Add("X-Agent-Type", "passthrough");
}).AddHttpMessageHandler<EndpointRoutingHandler>();
builder.Services.AddHttpClient<ConversationSummarizationStage>(client =>
{
    client.BaseAddress = new Uri(ollamaConfig.OllamaUrl);
    client.Timeout = TimeSpan.FromMinutes(2);
    client.DefaultRequestHeaders.Add("X-Agent-Type", "passthrough");
}).AddHttpMessageHandler<EndpointRoutingHandler>();
builder.Services.AddHttpClient<SemanticRelevanceFilterStage>(client =>
{
    client.BaseAddress = new Uri(ollamaConfig.OllamaUrl);
    client.Timeout = TimeSpan.FromMinutes(2);
    client.DefaultRequestHeaders.Add("X-Agent-Type", "passthrough");
}).AddHttpMessageHandler<EndpointRoutingHandler>();
builder.Services.AddHttpClient<TokenBudgetStage>(client =>
{
    client.BaseAddress = new Uri(ollamaConfig.OllamaUrl);
    client.Timeout = TimeSpan.FromMinutes(2);
    client.DefaultRequestHeaders.Add("X-Agent-Type", "passthrough");
}).AddHttpMessageHandler<EndpointRoutingHandler>();

// CodeHelper HTTP clients — passthrough suppresses agent switching (same as pipeline stages above)
builder.Services.AddHttpClient<TaskClassifier>(client =>
{
    client.BaseAddress = new Uri(ollamaConfig.OllamaUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.Add("X-Agent-Type", "passthrough");
}).AddHttpMessageHandler<EndpointRoutingHandler>();
builder.Services.AddSingleton<TaskClassifier>();
builder.Services.AddSingleton<PlanBuilder>(); // now uses OllamaService (already registered above)

// ── AgentHelpers ──
// Client-specific adapters (priority 90) are registered before model-family adapters
// (priority 60–80) so ClientName detection wins over model-hint detection.
builder.Services
    .AddAgentHelpers(ollamaConfig.OllamaModel)
    .AddAdapter<CopilotClientAdapter>()
    .AddAdapter<ContinueClientAdapter>()
    .AddAdapter<CursorClientAdapter>()
    .AddAdapter<AnthropicAgentAdapter>()
    .AddAdapter<QwenAgentAdapter>()
    .AddAdapter<DeepSeekAgentAdapter>()
    .AddAdapter<OpenSourceAgentAdapter>();

// Bridges caller detection (AgentHelpers.Core) to behavior config (TokenSaver.Config) — the two
// projects don't reference each other, so this resolver lives in the Host. Detects the calling
// model family per-request and resolves the effective agentConfig section for it. See
// AgentConfigResolver for why this runs per-request instead of once at session start: the server
// has no MCP session store, so "session start" and "this request" are the same thing here.
builder.Services.AddSingleton<AgentConfigResolver>();

// Same bridge pattern as AgentConfigResolver, but for the lazyContext section: decides
// ContextFormat.WillAskForMissingContext per calling client, so context-building tools know
// whether it's safe to send summaries and let the client re-fetch, or whether they must send
// complete content because nothing omitted is ever seen again. See LazyContextResolver's doc
// comment for why Cursor/Copilot/Continue are excluded from user override entirely.
builder.Services.AddSingleton<LazyContextResolver>();

// ── Fault tracking ──
// Records unhandled exceptions from any request (MCP tools/call included) so /health can
// reflect "something threw recently" even when DraftModel/models are otherwise reachable.
// See UnhandledFaultTracker/FaultTrackingMiddleware/UnhandledFaultHealthCheck.
builder.Services.AddSingleton<UnhandledFaultTracker>();

// ── Health Checks ──
// draftmodel is tagged "ready" (gates /health/ready — is the server usable right now?).
// unhandled-fault and self-test are deliberately untagged: they participate in the full
// /health report (liveness + self-diagnostics) but shouldn't flip /health/ready to
// unavailable just because a past request threw or a config sanity check failed — mirrors
// DraftModel.Loader's RouterHttpServer split between /health and /health/ready.
builder.Services.AddHealthChecks()
    .AddCheck<DraftModelHealthCheck>("draftmodel", tags: ["ready"])
    .AddCheck<UnhandledFaultHealthCheck>("unhandled-fault")
    .AddCheck<SelfTestHealthCheck>("self-test");

// ── MCP Server ──
// Both SSE (GET) and streamable-http (POST) are supported on the same /mcp endpoint.
// Stateless=false allows SSE clients (like Sixth) to connect while still handling
// streamable-http requests from clients like Continue.
// When launched via stdio (e.g. by Claude Desktop), stdio transport is added alongside HTTP
// so Claude manages the process lifecycle while other clients still use the HTTP endpoint.
IMcpServerBuilder mcpBuilder = builder.Services.AddMcpServer(options =>
    {
        options.ServerInstructions = DocResourceService.GetAgent();
    })
    .WithHttpTransport(o => { o.Stateless = true; })
    .WithTools<LocalModelHelperTools>()
    .WithTools<AccuracyTestTools>()
    .WithTools<ContextDiagnosticsTools>()
    .WithTools<CodeHelperTools>()
    .WithTools<AutoWorkflowTools>()
    .WithTools<HelpTools>()
    .WithTools<TokenEconomyTools>()
    .WithTools<SessionTools>()
    .WithTools<DevLoopTools>()
    .WithTools<SemanticIndexTools>()
    .WithTools<ManageConfigTools>()
    .WithResources<DocResourceService>()
    .WithPrompts<BuildConciseContextPrompt>();

if (isStdio)
{
    _ = mcpBuilder.WithStdioServerTransport();
}

WebApplication app = builder.Build();

// ── Fault tracking middleware ──
// Registered first so it wraps every other middleware and the MCP endpoint itself — the goal
// is to catch an unhandled exception from anywhere on this server, not just one subsystem.
// Re-throws after recording, so response behavior (500, etc.) is unchanged.
app.UseMiddleware<FaultTrackingMiddleware>();

// ── Agent detection middleware ──
// Runs before ContextInterceptMiddleware; peeks the MCP initialize handshake to detect the
// calling model family and log the agentConfig settings that now apply (prompt caching, context
// editing, KV-cache tuning — see AgentConfigResolver). Observational only; never blocks.
app.UseMiddleware<AgentDetectionMiddleware>();

// ── Context intercept middleware ──
app.UseMiddleware<ContextInterceptMiddleware>();

// ── Map MCP endpoint ──
app.MapMcp("/mcp");

// ── Map provider-registration endpoint (disabled, kept for compatibility) ──
// AiProviderConfigService no longer detects or writes any provider configs — this always
// returns an empty list now. Kept so existing callers (VS Code extension, RegisterMcpProviders
// tool) don't 404. Use the "TokenSaver: Open Install Prompt" VS Code command / INSTALL_PROMPT.md
// for the manual replacement.
//
// Optional query params (retained but unused by the no-op implementation):
//   claudeProjectPath — path to the TokenSaver.MCP binary (sent by the VS Code extension).
app.MapGet("/api/register-providers", async (
    AiProviderConfigService aiProviders,
    IOptions<TokenSaverConfig> opts,
    HttpContext httpCtx) =>
{
    int port = opts.Value.ServerPort;
    string workspaceRoot = opts.Value.WorkspaceRoot;

    // Resolve the server exe path for Sixth AI's stdio config entry.
    // The VS Code extension passes its mcpExePath as "claudeProjectPath"; fall back to
    // the process's own main module when the extension doesn't supply one (e.g. manual curl).
    string serverExePath = httpCtx.Request.Query["claudeProjectPath"].FirstOrDefault()
                        ?? httpCtx.Request.Query["serverExePath"].FirstOrDefault()
                        ?? Process.GetCurrentProcess().MainModule?.FileName
                        ?? "";

    IReadOnlyList<ConfigUpdateResult> results = await aiProviders.UpdateAllProvidersAsync(
        port, workspaceRoot: workspaceRoot, serverExePath: serverExePath);
    return Results.Ok(results.Select(r => new
    {
        provider = r.Provider.ToString(),
        success = r.Success,
        wasAlreadyConfigured = r.WasAlreadyConfigured,
        configPath = r.ConfigPath,
        message = r.Message
    }));
});

// ── Map health endpoint ──
// GET /health        → liveness + self-diagnostics (is the process up, and has anything thrown
//                      or failed a self-test recently — see UnhandledFaultHealthCheck,
//                      SelfTestHealthCheck). Runs every registered check (no Predicate).
// GET /health/ready  → readiness (is DraftModel router reachable and has models?). Only runs
//                      "ready"-tagged checks, so a past unhandled exception or a self-test
//                      failure doesn't flip readiness — mirrors DraftModel.Loader's
//                      RouterHttpServer split between /health and /health/ready.
// Both use the same JSON response writer (previously /health/ready only) so lastError/self-test
// detail from the new checks is actually visible to callers instead of a bare plaintext status.
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = WriteJsonHealthResponse
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = WriteJsonHealthResponse
});

// ── Log startup info ──
ILogger<Program> logger = app.Services.GetRequiredService<ILogger<Program>>();
TokenSaverConfig config = app.Services.GetRequiredService<IOptions<TokenSaverConfig>>().Value;

if (!string.IsNullOrWhiteSpace(config.WorkspaceRoot))
{
    Environment.SetEnvironmentVariable("DRAFTMODEL_WORKSPACE", config.WorkspaceRoot);

    // Process-start bootstrap: create .tokensaver/config.json (fully commented template),
    // .ignore-additional, and .ignore-bypass if they don't exist yet, so the workspace has
    // discoverable/editable config files from the start instead of relying purely on
    // missing-file-means-default behavior. Also re-run on every later MCP session connect (see
    // AgentDetectionMiddleware) so a file deleted after startup gets recreated too.
    WorkspaceConfigDefaults.EnsureDefaults(config.WorkspaceRoot, logger);
}

// Apply .tokensaver/config.json behavior-config overrides onto CodeHelperConfig/TokenEconomyConfig
// (in place, live-reloaded on every subsequent config-file change — see BehaviorConfigOverlay).
app.Services.GetRequiredService<BehaviorConfigOverlay>().Apply();

string transportLabel = isStdio ? "stdio+http" : "http";
Program.LogServerStarting(logger, config.ServerPort, config.OllamaUrl, config.WorkspaceRoot, transportLabel);

// ── Warm up unified index and build initial context ──
// Per the UnifiedIndex plan (§5): single call WorkspaceIndexService.ReconcileAndWarmAsync()
// does git-staleness reconciliation then builds/refreshes descriptions only for what's stale.
// Both operations are skipped when WorkspaceRoot is not configured.
if (!string.IsNullOrWhiteSpace(config.WorkspaceRoot))
{
    // Indexing is CPU-only — start immediately without waiting for DraftModel.
    _ = Task.Run(async () =>
    {
        try
        {
            WorkspaceIndexService indexService = app.Services.GetRequiredService<WorkspaceIndexService>();
            IndexStats stats = await indexService.IndexWorkspaceAsync(config.WorkspaceRoot);
            Program.LogUnifiedIndexReady(
                logger, stats.FilesIndexed, stats.SymbolsIndexed, stats.FilesSkipped, stats.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            Program.LogInitialIndexSkipped(logger, ex);
        }
    });

    // Context warm-up calls the full pipeline (PromptAssessor, SemanticFilter, etc.)
    // which all need DraftModel — wait until it's reachable before proceeding. Gated by
    // ContextHelperConfig.AutoWarmOnStartup (behavior-config field "context.autoWarmOnStartup",
    // on by default — see BehaviorConfigOverlay.Apply() above, which already ran).
    if (app.Services.GetRequiredService<IOptions<ContextHelperConfig>>().Value.AutoWarmOnStartup)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                OllamaService ollama = app.Services.GetRequiredService<OllamaService>();
                bool ready = await ollama.WaitForReadyAsync(
                    TimeSpan.FromMinutes(10),
                    TimeSpan.FromSeconds(10),
                    logger);

                if (!ready)
                {
                    Program.LogDraftModelNotAvailable(logger);
                    return;
                }

                ContextPipelineService contextPipeline = app.Services.GetRequiredService<ContextPipelineService>();
                BuildContextResult result = await contextPipeline.BuildConciseContextAsync(
                    "[agent boot] initialize context cache",
                    sessionId: "agent-boot");
                Program.LogAgentContextWarmed(logger, result.GeneratedFiles.Count, result.TokenCount);
            }
            catch (Exception ex)
            {
                Program.LogContextWarmupSkipped(logger, ex);
            }
        });
    }
}
else
{
    Program.LogWorkspaceRootNotConfigured(logger);
}

// Listen on * to bind both IPv4 (0.0.0.0) and IPv6 (::) so that clients
// resolving "localhost" to ::1 can connect alongside IPv4-only callers.
try
{
    app.Run($"http://*:{config.ServerPort}");
}
catch (IOException ex) when (ex.InnerException is AddressInUseException)
{
    Program.LogPortInUse(logger, config.ServerPort);
    Environment.Exit(1);
}

// ── Health response writer ────────────────────────────────────────────────────
static async Task WriteJsonHealthResponse(HttpContext ctx, HealthReport report)
{
    ctx.Response.ContentType = "application/json";
    var result = new
    {
        status = report.Status.ToString(),
        checks = report.Entries.Select(e => new
        {
            name = e.Key,
            status = e.Value.Status.ToString(),
            description = e.Value.Description,
            error = e.Value.Exception?.Message,
            data = e.Value.Data.Count > 0 ? e.Value.Data : null
        }),
        totalDuration = report.TotalDuration.TotalMilliseconds
    };
    await ctx.Response.WriteAsJsonAsync(result);
}

// ── Source-generated logging (CA1873) ──────────────────────────────────────────
// Top-level statement programs compile into an implicit `partial class Program`,
// so LoggerMessage source-gen methods can be declared here directly. Must NOT be
// declared `static` here — the implicitly generated Program class is a normal
// (non-static) internal partial class; adding `static` on this partial forces the
// merged type to become static, which then breaks any usage of Program as a type
// argument elsewhere (e.g. ILogger<Program>) with CS0718.
internal partial class Program
{
    [LoggerMessage(Level = LogLevel.Information, Message = "TokenSaver MCP server starting on port {Port}, Ollama router at {Url}, workspace {Workspace}, transport {Transport}")]
    internal static partial void LogServerStarting(ILogger logger, int port, string url, string workspace, string transport);

    [LoggerMessage(Level = LogLevel.Information, Message = "Unified index ready: {Files} files, {Symbols} symbols ({Skipped} unchanged) in {Elapsed:F1}s")]
    internal static partial void LogUnifiedIndexReady(ILogger logger, int files, int symbols, int skipped, double elapsed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Initial unified index skipped")]
    internal static partial void LogInitialIndexSkipped(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "DraftModel not available after 10 min — context warm-up skipped.")]
    internal static partial void LogDraftModelNotAvailable(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent context warmed: {Generated} generated files, {Tokens} tokens")]
    internal static partial void LogAgentContextWarmed(ILogger logger, int generated, int tokens);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Context warm-up skipped")]
    internal static partial void LogContextWarmupSkipped(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "WorkspaceRoot not configured — context warm-up and code index skipped. Set TokenSaver:WorkspaceRoot in config or TOKENSAVER_TokenSaver__WorkspaceRoot env var.")]
    internal static partial void LogWorkspaceRootNotConfigured(ILogger logger);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Port {Port} already in use — another TokenSaver.MCP instance is likely already running. Exiting.")]
    internal static partial void LogPortInUse(ILogger logger, int port);
}

// ── EventLog source registration ──────────────────────────────────────────────
// Mirrors DraftModel.Loader/Program.cs's EventLogRegistration exactly (see that file for the
// full rationale) — only the source name differs ("TokenSaver" vs "DraftModel"), matching the
// TOKENSAVER_EVENT_LOG_SOURCE constant the VS Code extension polls for in
// pollTokenSaverServiceEventLog(). Creating a new Event Source the first time requires the
// registry write under HKLM\SYSTEM\CurrentControlSet\Services\EventLog\Application\<source> —
// the SCM runs this service as LocalSystem by default, which has that permission, so this
// succeeds unattended on first service start. Never blocks startup on failure: callers get back
// `false`/no-op and simply fall back to console-only logging, exactly as before this fix.
internal static class EventLogRegistration
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void TryAddTokenSaverEventLog(ILoggingBuilder builder)
    {
        if (!EnsureSourceExists("TokenSaver"))
        {
            return;
        }

        builder.AddEventLog(o =>
        {
            o.SourceName = "TokenSaver";
            o.LogName = "Application";
        });
    }

    private static bool EnsureSourceExists(string source)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            if (!System.Diagnostics.EventLog.SourceExists(source))
            {
                System.Diagnostics.EventLog.CreateEventSource(source, "Application");
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}