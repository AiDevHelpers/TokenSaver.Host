using TokenSaver.AgentHelpers.Anthropic;
using TokenSaver.AgentHelpers.Core.Extensions;
using TokenSaver.AgentHelpers.DeepSeek;
using TokenSaver.AgentHelpers.OpenSource;
using TokenSaver.AgentHelpers.Qwen;

using TokenSaver.CodeHelper.AutoWorkflow;
using TokenSaver.CodeHelper.Calibration;
using TokenSaver.CodeHelper.Configuration;
using TokenSaver.CodeHelper.Indexing;
using TokenSaver.CodeHelper.Patching;
using TokenSaver.CodeHelper.Planning;
using TokenSaver.CodeHelper.Research;
using TokenSaver.Config;
using TokenSaver.ContextHelper.Calibration;
using TokenSaver.ContextHelper.Configuration;
using TokenSaver.ContextHelper.Pipeline;
using TokenSaver.ContextHelper.Pipeline.Stages;
using TokenSaver.Docs;
using TokenSaver.Docs.Prompts;
using TokenSaver.Library.Configuration;
using TokenSaver.Library.Contracts;
using TokenSaver.Database.Context;
using TokenSaver.Library.Models.AiProviders;
using TokenSaver.Library.Services;
using TokenSaver.MCP.HealthChecks;
using TokenSaver.MCP.Middleware;
using TokenSaver.MCP.Services;
using TokenSaver.MCP.Tools;

using Logging.Core;

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

using Serilog.Events;

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
//
// TOKENSAVER_NO_SCM=1 (set by scripts/run-dev-server.ps1) forces isWindowsService to false
// outright — the dev loop must always behave as a plain script/console launch, never routed
// through Windows Service hosting, regardless of what WindowsServiceHelpers.IsWindowsService()'s
// own detection would say in whatever context it's invoked from (e.g. a non-interactive
// automation shell with no attached console looks the same to that heuristic as an actual SCM
// session).
bool isWindowsService = Environment.GetEnvironmentVariable("TOKENSAVER_NO_SCM") != "1"
                        && WindowsServiceHelpers.IsWindowsService();
bool isStdio = Console.IsInputRedirected
              && !isWindowsService
              && Environment.GetEnvironmentVariable("TOKENSAVER_HTTP_ONLY") != "1";
if (isStdio)
{
    Console.SetOut(Console.Error);
}

// ── Pause on unhandled startup failure (interactive console only) ──
// A crash during startup (bad --server-config path, malformed JSON, etc.) would otherwise print
// the exception and instantly close the window it's running in — unreadable in practice for any
// launch that runs in its own window (an elevated relaunch, a double-clicked script). Only wired
// up for a real interactive console: never under the SCM (nothing attached to read a keypress)
// and never when stdin is redirected (stdio-spawned MCP host, VS Code extension, CI) — those
// must fail fast, not hang waiting for input nobody can provide.
if (!isWindowsService && !Console.IsInputRedirected)
{
    AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"Unhandled exception: {e.ExceptionObject}");
        Console.Error.WriteLine("Press Enter to close this window...");
        try
        {
            _ = Console.ReadLine();
        }
        catch (InvalidOperationException)
        {
            // No console attached after all — nothing to wait on.
        }
    };
}

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// ── Optional external server config file ──
// --server-config <path> layers an additional JSON config file on top of the default
// appsettings.json (already loaded from the content root by CreateBuilder above), taking
// precedence over it. Defaults to nothing extra loaded — the server config location stays
// "where it is" (appsettings.json next to the exe) unless this is passed explicitly.
// scripts/run-dev-server.ps1 uses this to point the dev loop at .tokensaver/dev-appsettings.json
// instead of mutating the real appsettings.json — always as an absolute path (see below).
//
// Must be an absolute path. `dotnet run` sets Directory.GetCurrentDirectory() for the launched
// app to the project's source directory (same as IHostEnvironment.ContentRootPath), NOT the
// shell's actual working directory — there is no reliable way from inside the process to recover
// "the directory the user typed this command from". A relative path here would silently resolve
// under src\Host\TokenSaver.MCP\ instead of wherever the caller meant, so this fails fast with a
// clear message instead of a confusing deep FileNotFoundException out of AddJsonFile.
string? serverConfigPath = GetArgValue(args, "--server-config");
if (!string.IsNullOrWhiteSpace(serverConfigPath))
{
    if (!Path.IsPathRooted(serverConfigPath))
    {
        Console.Error.WriteLine(
            $"--server-config must be an absolute path (got '{serverConfigPath}'). " +
            "Relative paths resolve against the project's source directory under `dotnet run`, " +
            "not the shell's working directory — pass a full path instead.");
        if (!isWindowsService && !Console.IsInputRedirected)
        {
            Console.Error.WriteLine("Press Enter to close this window...");
            try
            {
                _ = Console.ReadLine();
            }
            catch (InvalidOperationException)
            {
                // No console attached after all — nothing to wait on.
            }
        }

        Environment.Exit(1);
    }

    builder.Configuration.AddJsonFile(serverConfigPath, optional: false, reloadOnChange: true);
}

// ── Windows Service support ──
// A no-op when launched normally (console, or spawned stdio by an MCP host) — only takes
// effect when the SCM launches this exe non-interactively, exactly like DraftModel.Loader's
// use of the same helper. Lets TokenSaver.MCP be installed as an always-on background
// service (HTTP mode, serving Copilot/Continue/Cursor on ServerPort) independent of whether
// Claude Desktop is running its own separate stdio-spawned instance via the .mcpb.
// Skipped outright when TOKENSAVER_NO_SCM=1 (see isWindowsService above) — the dev loop must
// never register with the SCM even if UseWindowsService's own detection would disagree.
if (isWindowsService)
{
    builder.Host.UseWindowsService(o => o.ServiceName = "TokenSaverMCP");
}

// Serilog-backed logging via TokenSaver.Common.Logger (shared with DraftModel.Loader — see
// Logging.Core.AppLoggerFactory). Providers: Console (single-line) + File
// (%ProgramData%\TokenSaver\logs\tokensaver-.log, rolling daily, retained 14 days) + EventLog
// (Windows only) + VS Code JSON sink (when VSCODE_LOGS is set). The File sink is what makes logs
// inspectable after the fact — previously only Console (and, under the SCM, EventLog) existed, so
// a crash before anyone thought to capture console output was unrecoverable.
//
// EventLog needs its source pre-created once (EnsureSourceExists is a no-op after the first
// success) — only attempted when running as the installed Windows service, since that's the
// context where console output is invisible and the VS Code extension's
// pollTokenSaverServiceEventLog() (which reads ProviderName='TokenSaver' from the Application
// log) needs something to poll. Never blocks startup on failure: a non-admin dev-loop launch
// just means the EventLog sink silently has nothing to write to yet.
if (isWindowsService && OperatingSystem.IsWindows())
{
    EventLogRegistration.EnsureTokenSaverEventLogSourceExists();
}

LogEventLevel minimumLevel =
    builder.Configuration.GetValue<bool>($"{TokenSaverConfig.SectionName}:VerboseLogging")
        ? LogEventLevel.Debug
        : LogEventLevel.Information;

builder.Logging.ClearProviders();
builder.Host.UseAppLogging(LogChannel.TokenSaver, minimumLevel, cfg =>
{
    // HttpClientFactory's built-in per-request logging (categories "System.Net.Http.HttpClient.*")
    // logs every failed request at Information level with the full exception ToString (stack
    // trace included) — e.g. a wall of text every time the DraftModel router is unreachable.
    // Silenced down to Warning: OllamaEndpointRegistry already logs one concise line right after
    // ("endpoint ... failed — marked unhealthy"), so nothing meaningful is lost, just the
    // redundant noise on top of it.
    cfg.MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning);
});

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
builder.Services.AddSingleton<RetrievalCalibrationService>();
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
builder.Services.AddSingleton<IRerankService, RerankService>();
builder.Services.AddSingleton<IConditionalRetrievalService, ConditionalRetrievalService>();
builder.Services.AddSingleton<SemanticIndexBackgroundTrigger>();

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

// WorkspaceRoot is not auto-detected at startup anymore — the server should come up and be
// usable (health checks, model warm-up) without needing to know which repo it's serving. If it's
// already known via explicit config/env (e.g. the VS Code extension passes
// TOKENSAVER_TokenSaver__WorkspaceRoot per-workspace-process), carry that value forward. Otherwise
// it stays empty until WorkspaceRootSessionResolver resolves it lazily on the first tool call of a
// connecting session, via that session's MCP roots capability (or a cwd repo-root walk as a
// fallback for clients that don't support roots) — see that type's doc comment.
if (!string.IsNullOrWhiteSpace(ollamaConfig.WorkspaceRoot))
{
    _ = builder.Services.PostConfigure<TokenSaverConfig>(cfg => cfg.WorkspaceRoot = ollamaConfig.WorkspaceRoot);
}

// TokenSaver:BehaviorConfigPath (defaults to "", meaning {workspaceRoot}/.tokensaver/config.json)
// is exported as TOKENSAVER_LOCAL_CONFIG_PATH so BehaviorConfigService reads/writes that file
// instead of the default. See BehaviorConfigService.LocalConfigPath.
if (!string.IsNullOrWhiteSpace(ollamaConfig.BehaviorConfigPath))
{
    Environment.SetEnvironmentVariable("TOKENSAVER_LOCAL_CONFIG_PATH", ollamaConfig.BehaviorConfigPath);
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
builder.Services.AddHttpClient<RetrievalCalibrationService>(client =>
{
    client.BaseAddress = new Uri(ollamaConfig.OllamaUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
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

// Implements repoHelper.optimizeInstructions: on session connect, copies the machine-global
// Claude instructions into whatever workspace instruction file the connecting tool actually
// supports (see InstructionSyncResolver for the per-tool target lookup). Off by default.
builder.Services.AddSingleton<InstructionOptimizerService>();

// Resolves WorkspaceRoot lazily on the first tool call of a connecting session (MCP roots
// capability, falling back to a cwd repo-root walk) instead of eagerly at process startup — see
// WorkspaceRootSessionResolver's doc comment.
builder.Services.AddSingleton<WorkspaceRootSessionResolver>();

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
    // .ignore-additional, .ignore-bypass, and the machine-global config.json if they don't exist
    // yet, so the workspace has discoverable/editable config files from the start instead of
    // relying purely on missing-file-means-default behavior. Also re-run on every later MCP
    // session connect (see AgentDetectionMiddleware) so a file deleted after startup gets
    // recreated too.
    WorkspaceConfigDefaults.EnsureDefaults(config.WorkspaceRoot, logger);

    // Config-upgrade pass: a config.json that already exists (created by an older TokenSaver
    // version) may be missing top-level sections the current schema knows about. Patches those
    // in without touching anything the user already set — see ConfigUpgradeService.
    ConfigUpgradeService.UpgradeIfNeeded(config.WorkspaceRoot, logger);
}
else
{
    Program.LogWorkspaceRootNotConfigured(logger);
}

// Apply .tokensaver/config.json behavior-config overrides onto CodeHelperConfig/TokenEconomyConfig
// (in place, live-reloaded on every subsequent config-file change — see BehaviorConfigOverlay).
app.Services.GetRequiredService<BehaviorConfigOverlay>().Apply();

string transportLabel = isStdio ? "stdio+http" : "http";
Program.LogServerStarting(logger, config.ServerPort, config.OllamaUrl, config.WorkspaceRoot, transportLabel);

// ── Warm the model on start ──
// Independent of WorkspaceRoot: forces DraftModel/Ollama to load a model into memory so the
// first real tool call isn't the one paying that cold-start cost. Gated by
// ContextHelperConfig.AutoWarmOnStartup (behavior-config field "context.autoWarmOnStartup", on by
// default — see BehaviorConfigOverlay.Apply() above, which already ran).
// Indexing and the full context-pipeline warm-up (which both need a workspace) are NOT started
// here anymore — they're deferred to WorkspaceRootSessionResolver, which fires them once a
// session actually resolves a workspace root (see that type's doc comment).
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
            }
        }
        catch (Exception ex)
        {
            Program.LogContextWarmupSkipped(logger, ex);
        }
    });
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

// ── CLI arg parsing ────────────────────────────────────────────────────────────
static string? GetArgValue(string[] args, string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "DraftModel not available after 10 min — model warm-up skipped.")]
    internal static partial void LogDraftModelNotAvailable(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Model warm-up skipped")]
    internal static partial void LogContextWarmupSkipped(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "WorkspaceRoot not configured at startup — indexing and context warm-up deferred until a connecting session resolves one (see WorkspaceRootSessionResolver).")]
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
// `false`/no-op and Logging.Core.AppLoggerFactory's EventLog sink simply has nothing to write to
// yet. The actual EventLog *write path* now goes through that Serilog sink (added by
// UseAppLogging above) rather than an ILoggingBuilder.AddEventLog provider — this method only
// needs to ensure the source exists for that sink to succeed.
internal static class EventLogRegistration
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static bool EnsureTokenSaverEventLogSourceExists()
    {
        try
        {
            if (!System.Diagnostics.EventLog.SourceExists("TokenSaver"))
            {
                System.Diagnostics.EventLog.CreateEventSource("TokenSaver", "Application");
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}