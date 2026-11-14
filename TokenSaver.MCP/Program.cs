// ── Transport & Startup Setup ──
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

using Serilog.Events;

using System.Collections.ObjectModel;
using System.Diagnostics;

using SperryDev.Logger.Configuration;
using TokenSaver.Common.Core.Configuration;
using TokenSaver.Common.Installation;
using TokenSaver.Common.Models;
using TokenSaver.Config;
using TokenSaver.Database.Context;
using TokenSaver.Docs;
using TokenSaver.Docs.Prompts;
using TokenSaver.CodeHelper.Indexing;
using TokenSaver.MCP.HealthChecks;
using TokenSaver.MCP.Middleware;
using TokenSaver.MCP.Services;
using TokenSaver.MCP.Tools;
using TokenSaver.MCP;
using TokenSaver.MCP.Endpoints;
using TokenSaver.OllamaIntegration.Services;

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

// ── Record fatal and unobserved exceptions ──
// Written to %ProgramData%\TokenSaver\logs\fatal.log and the Application event log, so they
// survive even under the SCM, where nothing reads the console. Without these an unhandled
// exception in a background task or a crash during startup leaves no trace.
AppDomain.CurrentDomain.UnhandledException += (_, e) => Program.WriteFatal("UnhandledException", e.ExceptionObject);
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    Program.WriteFatal("UnobservedTaskException", e.Exception);
    e.SetObserved();
};

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

// ═══════════════════════════════════════════════════════════════════════════════
// ── BUILD & CONFIGURE SERVICES ──
// ═══════════════════════════════════════════════════════════════════════════════

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// ── Load optional external server config file ──
// --server-config <path> layers an additional JSON config file on top of the default
// appsettings.json. Must be an absolute path. See below for rationale.
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

        Program.WriteFatal("BadArgument", $"--server-config must be an absolute path (got '{serverConfigPath}').");
        Environment.Exit(1);
    }

    builder.Configuration.AddJsonFile(serverConfigPath, optional: false, reloadOnChange: true);
}

// ── Windows Service support ──
// A no-op when launched normally (console, or spawned stdio by an MCP host) — only takes
// effect when the SCM launches this exe non-interactively.
if (isWindowsService)
{
    builder.Host.UseWindowsService(o => o.ServiceName = "TokenSaverMCP");
}

// ── Configure logging ──
// Serilog-backed logging via SperryDev.Logger (shared with SimpleModelServer).
// Providers: Console (single-line) + File (%ProgramData%\TokenSaver\logs) + EventLog + VS Code JSON
if (isWindowsService && OperatingSystem.IsWindows())
{
    EventLogRegistration.EnsureTokenSaverEventLogSourceExists();
}

LogEventLevel minimumLevel =
    builder.Configuration.GetValue<bool>($"{TokenSaverConfig.SectionName}:VerboseLogging")
        ? LogEventLevel.Debug
        : LogEventLevel.Information;

builder.Logging.ClearProviders();
builder.Host.UseAppLogging("TokenSaver", minimumLevel, cfg =>
{
    cfg.MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning);
    cfg.MinimumLevel.Override("Microsoft.Extensions.Diagnostics.HealthChecks", LogEventLevel.Warning);
});

// ── Configure TokenSaver ──
builder.Services.Configure<TokenSaverConfig>(
    builder.Configuration.GetSection(TokenSaverConfig.SectionName));
builder.Configuration.AddEnvironmentVariables("LOCALHELPER_");
builder.Configuration.AddEnvironmentVariables("TOKENSAVER_");

// ── HTTP context access ──
builder.Services.AddHttpContextAccessor();

// ── Resolve Ollama configuration ──
TokenSaverConfig ollamaConfig = builder.Configuration
    .GetSection(TokenSaverConfig.SectionName)
    .Get<TokenSaverConfig>() ?? new TokenSaverConfig();

// Propagate resolved configuration back into DI
string resolvedOllamaUrl = ollamaConfig.OllamaUrl;
builder.Services.PostConfigure<TokenSaverConfig>(cfg => cfg.OllamaUrl = resolvedOllamaUrl);

Collection<OllamaEndpointConfig> resolvedEndpoints = ollamaConfig.Endpoints;
builder.Services.PostConfigure<TokenSaverConfig>(cfg => cfg.Endpoints = resolvedEndpoints);

// WorkspaceRoot is resolved lazily by WorkspaceRootSessionResolver on first tool call
if (!string.IsNullOrWhiteSpace(ollamaConfig.WorkspaceRoot))
{
    builder.Services.PostConfigure<TokenSaverConfig>(cfg => cfg.WorkspaceRoot = ollamaConfig.WorkspaceRoot);
}

// Behavior config path (defaults to {workspaceRoot}/.tokensaver/config.json)
if (!string.IsNullOrWhiteSpace(ollamaConfig.BehaviorConfigPath))
{
    Environment.SetEnvironmentVariable("TOKENSAVER_LOCAL_CONFIG_PATH", ollamaConfig.BehaviorConfigPath);
}

// ═══════════════════════════════════════════════════════════════════════════════
// ── REGISTER SERVICES (via extension methods) ──
// ═══════════════════════════════════════════════════════════════════════════════

// Compute User-Agent header with version for SimpleModelServer requests
string version = VersionDetector.GetVersionString();
string userAgent = $"TokenSaver/{version}";

// Stamp the User-Agent on EVERY HttpClient the factory produces — including the unnamed
// clients created via IHttpClientFactory.CreateClient() by the background probes
// (EndpointHealthMonitor's /api/tags poll, McpRegistrationCheckService's /health poll). Without
// this, those probes reach SimpleModelServer's router with no User-Agent and show up as an
// unidentified caller in its request log. Applying it here (rather than per-typed-client) keeps
// the identifier in one place and guarantees any future HttpClient is attributed too.
builder.Services.ConfigureHttpClientDefaults(http =>
    http.ConfigureHttpClient(client =>
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent)));

builder.Services
    .AddConfigurationSections(builder.Configuration)
    .AddContextPipeline()
    .AddCodeHelperServices()
    .AddIndexingServices()
    .AddMcpCoreServices()
    .AddTokenEconomyServices()
    .AddOllamaHttpClients(ollamaConfig)
    .AddPipelineHttpClients(ollamaConfig)
    .AddAgentHelpersAdapters(ollamaConfig.OllamaModel)
    .AddWorkspaceSessionServices()
    .AddDebuggerServices()
    .AddWslControlServices()
    .AddLanguageHelperServices();

// ── Health Checks ──
// simplemodelserver is tagged "ready" (gates /health/ready); others are untagged.
builder.Services.AddHealthChecks()
    .AddCheck<SimpleModelServerHealthCheck>("simplemodelserver", tags: ["ready"])
    .AddCheck<UnhandledFaultHealthCheck>("unhandled-fault")
    .AddCheck<SelfTestHealthCheck>("self-test");

// ── MCP Server ──
// Both SSE (GET) and streamable-http (POST) are supported on the same /mcp endpoint.
// Stateless=true allows SSE clients while still handling streamable-http from others.
IMcpServerBuilder mcpBuilder = builder.Services.AddMcpServer(options =>
    {
        options.ServerInstructions = DocResourceService.GetAgent();
    })
    .WithHttpTransport(o => { o.Stateless = true; })
    .WithTools<LocalModelHelperTools>()
    .WithTools<AccuracyTestTools>()
    .WithTools<ContextDiagnosticsTools>()
    .WithTools<CodeHelperTools>()
    .WithTools<IndexChangeTools>()
    .WithTools<AutoWorkflowTools>()
    .WithTools<HelpTools>()
    .WithTools<TokenEconomyTools>()
    .WithTools<SessionTools>()
    .WithTools<DevLoopTools>()
    .WithTools<SemanticIndexTools>()
    .WithTools<ManageConfigTools>()
    .WithTools<IDELauncherTools>()
    .WithTools<DebugOutputTools>()
    .WithTools<DebuggerControlTools>()
    .WithTools<DebuggerAnalysisTools>()
    .WithTools<TestExecutionTools>()
    .WithTools<WslControlTools>()
    .WithTools<LanguageHelperTools>()
    .WithTools<InstructionTools>()
    .WithResources<DocResourceService>()
    .WithPrompts<BuildConciseContextPrompt>();

if (isStdio)
{
    _ = mcpBuilder.WithStdioServerTransport();
}

// ═══════════════════════════════════════════════════════════════════════════════
// ── BUILD APP & CONFIGURE MIDDLEWARE ──
// ═══════════════════════════════════════════════════════════════════════════════

WebApplication app = builder.Build();

// ── Middleware pipeline ──
// Ordered for correct exception/context handling:
// 1. DebuggerContext (captures extension info and timing)
// 2. FaultTracking (wraps everything)
// 3. AgentDetection (peeks handshake for model family)
// 4. ContextIntercept (intercepts/modifies context)
app.UseDebuggerContextInterceptor();
app.UseMiddleware<FaultTrackingMiddleware>();
app.UseMiddleware<AgentDetectionMiddleware>();
app.UseMiddleware<ContextInterceptMiddleware>();

// ── Map MCP endpoint ──
app.MapMcp("/mcp");

// ── Map Debugger API endpoints (for VS Code extension) ──
app.MapDebuggerEndpoints();

// ── Map Debugger Streaming endpoints (Server-Sent Events) ──
app.MapDebuggerStreamingEndpoints();

// ── Map provider-registration endpoint (disabled, compatibility only) ──
app.MapGet("/api/register-providers", async (
    AiProviderConfigService aiProviders,
    IOptions<TokenSaverConfig> opts,
    HttpContext httpCtx) =>
{
    int port = opts.Value.ServerPort;
    string workspaceRoot = opts.Value.WorkspaceRoot;

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

// ── Map health endpoints ──
// GET /health → liveness + self-diagnostics (is the process up, is anything broken?)
// GET /health/ready → readiness (is SimpleModelServer reachable, are models loaded?)
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = WriteJsonHealthResponse
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = WriteJsonHealthResponse
});

// ═══════════════════════════════════════════════════════════════════════════════
// ── STARTUP & WARMUP ──
// ═══════════════════════════════════════════════════════════════════════════════

using (IServiceScope scope = app.Services.CreateScope())
{
    ILogger<Program> logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    TokenSaverConfig config = scope.ServiceProvider.GetRequiredService<IOptions<TokenSaverConfig>>().Value;

    string transportMode = isStdio ? "stdio+http" : "http";
    Program.LogServerStarting(logger, config.ServerPort, config.OllamaUrl, config.WorkspaceRoot ?? "(none)", transportMode);

    // Warm up model/index asynchronously (non-blocking)
    // The workspace index service is registered but warmup is deferred until first tool call
    // when WorkspaceRoot is actually known (resolved by WorkspaceRootSessionResolver).
}

// ── Log who asked for shutdown ──
// The Windows SCM does not report which process sent a stop, so a client that stops the service
// on purpose (the VS Code extension) writes %ProgramData%\TokenSaver\stop-request.json first. The
// stop logged here reads that file; when it is missing the stop came from outside those clients.
{
    IHostApplicationLifetime lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    ILogger<Program> stopLogger = app.Services.GetRequiredService<ILogger<Program>>();
    DateTimeOffset startedAt = DateTimeOffset.UtcNow;
    _ = lifetime.ApplicationStopping.Register(() =>
        Program.LogShutdownRequested(stopLogger, (DateTimeOffset.UtcNow - startedAt).TotalSeconds, ReadAndClearStopRequest()));
}

// ── Start the server ──
// Listen on * to bind both IPv4 (0.0.0.0) and IPv6 (::) so that clients
// resolving "localhost" to ::1 can connect alongside IPv4-only callers.
try
{
    TokenSaverConfig config = app.Services.GetRequiredService<IOptions<TokenSaverConfig>>().Value;
    app.Run($"http://*:{config.ServerPort}");
}
catch (IOException ex) when (ex.InnerException is AddressInUseException)
{
    using (IServiceScope scope = app.Services.CreateScope())
    {
        ILogger<Program> logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        TokenSaverConfig config = scope.ServiceProvider.GetRequiredService<IOptions<TokenSaverConfig>>().Value;
        Program.LogPortInUse(logger, config.ServerPort);
    }

    Environment.Exit(1);
}
catch (Exception ex)
{
    // Anything that stops the server after startup (host failure, fatal error in a hosted service)
    // is recorded before the process exits; the exception then propagates as before.
    Program.WriteFatal("ServerStopped", ex);
    throw;
}

// ═══════════════════════════════════════════════════════════════════════════════
// ── HELPER FUNCTIONS ──
// ═══════════════════════════════════════════════════════════════════════════════

static string? GetArgValue(string[] args, string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

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

// ═══════════════════════════════════════════════════════════════════════════════
// ── SOURCE-GENERATED LOGGING & UTILITIES ──
// ═══════════════════════════════════════════════════════════════════════════════

internal partial class Program
{
    [LoggerMessage(Level = LogLevel.Information, Message = "TokenSaver MCP server starting on port {Port}, Ollama router at {Url}, workspace {Workspace}, transport {Transport}")]
    internal static partial void LogServerStarting(ILogger logger, int port, string url, string workspace, string transport);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SimpleModelServer not available after 10 min — model warm-up skipped.")]
    internal static partial void LogSimpleModelServerNotAvailable(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Model warm-up skipped")]
    internal static partial void LogContextWarmupSkipped(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Port {Port} already in use — another TokenSaver.MCP instance is likely already running. Exiting.")]
    internal static partial void LogPortInUse(ILogger logger, int port);

    /// <summary>
    ///     Records a fatal or unobserved error to %ProgramData%\TokenSaver\logs\fatal.log and the
    ///     Application event log (source TokenSaver). Usable before the DI logger exists and under the
    ///     SCM, where a console write is lost. Never throws.
    /// </summary>
    internal static void WriteFatal(string kind, object error)
    {
        string text = $"{DateTimeOffset.Now:O} [{kind}] {error}";
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TokenSaver", "logs");
            _ = Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "fatal.log"), text + Environment.NewLine);
        }
        catch (IOException)
        {
            // Disk full or locked: the event log entry below still goes out.
        }
        catch (UnauthorizedAccessException)
        {
            // No rights to the logs folder: the event log entry below still goes out.
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                System.Diagnostics.EventLog.WriteEntry("TokenSaver", text, System.Diagnostics.EventLogEntryType.Error);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Source not registered (interactive run): the file entry above is the record.
            }
            catch (InvalidOperationException)
            {
                // Event log unavailable: the file entry above is the record.
            }
            catch (System.Security.SecurityException)
            {
                // Not permitted to write to the event log: the file entry above is the record.
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shutdown requested after {UptimeSeconds:F0}s uptime — {Source}")]
    internal static partial void LogShutdownRequested(ILogger logger, double uptimeSeconds, string source);

    /// <summary>
    ///     Reads and deletes the stop request a client left in %ProgramData%\TokenSaver\stop-request.json.
    ///     Returns a description of who asked for the stop, or that no request was recorded.
    /// </summary>
    internal static string ReadAndClearStopRequest()
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TokenSaver", "stop-request.json");
        try
        {
            if (!File.Exists(path))
            {
                return "no stop request recorded (SCM stop from outside TokenSaver clients, or host shutdown)";
            }

            string json = File.ReadAllText(path);
            File.Delete(path);
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
            System.Text.Json.JsonElement root = doc.RootElement;
            string Field(string name) =>
                root.TryGetProperty(name, out System.Text.Json.JsonElement value) ? value.ToString() : "?";
            return $"caller={Field("caller")}, reason={Field("reason")}, requestedAt={Field("requestedAt")}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return $"stop request file unreadable: {ex.Message}";
        }
    }
}

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
