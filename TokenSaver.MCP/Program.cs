// ── Transport & Startup Setup ──
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

using Serilog.Events;

using System.Collections.ObjectModel;
using System.Diagnostics;

using TokenSaver.Common.Logging;
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
Program.RegisterConsolePauseOnUnhandledException(isWindowsService);

// ═══════════════════════════════════════════════════════════════════════════════
// ── BUILD & CONFIGURE SERVICES ──
// ═══════════════════════════════════════════════════════════════════════════════

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

TokenSaverHostBootstrap.ConfigureHostConfiguration(builder, args, isWindowsService);
TokenSaverConfig ollamaConfig = TokenSaverHostBootstrap.ConfigureOllamaAndHttpServices(builder);
TokenSaverHostRegistration.RegisterTokenSaverServices(builder, ollamaConfig, isStdio);

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

app.MapTokenSaverEndpoints(WriteJsonHealthResponse);

// ═══════════════════════════════════════════════════════════════════════════════
// ── STARTUP & WARMUP ──
// ═══════════════════════════════════════════════════════════════════════════════

TokenSaverHostBootstrap.LogServerStarting(app, isStdio);

// ── Log who asked for shutdown ──
// The Windows SCM does not report which process sent a stop, so a client that stops the service
// on purpose (the VS Code extension) writes %ProgramData%\TokenSaver\stop-request.json first. The
// stop logged here reads that file; when it is missing the stop came from outside those clients.
TokenSaverHostBootstrap.RegisterShutdownLogging(app);

// ── Start the server ──
// Listen on * to bind both IPv4 (0.0.0.0) and IPv6 (::) so that clients
// resolving "localhost" to ::1 can connect alongside IPv4-only callers.
TokenSaverHostBootstrap.RunServer(app);

// ═══════════════════════════════════════════════════════════════════════════════
// ── HELPER FUNCTIONS ──
// ═══════════════════════════════════════════════════════════════════════════════

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
    /// <summary>
    ///     Keeps an interactive console window open after a fatal error so the message can be read.
    ///     Never under the SCM or when stdin is redirected (those must fail fast). Never throws.
    /// </summary>
    internal static void PauseForConsoleIfInteractive(bool isWindowsService)
    {
        if (isWindowsService || Console.IsInputRedirected)
        {
            return;
        }

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

    /// <summary>Writes an unhandled exception to the console and waits for Enter (interactive launches only).</summary>
    internal static void RegisterConsolePauseOnUnhandledException(bool isWindowsService)
    {
        if (isWindowsService || Console.IsInputRedirected)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Unhandled exception: {e.ExceptionObject}");
            PauseForConsoleIfInteractive(isWindowsService: false);
        };
    }

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
