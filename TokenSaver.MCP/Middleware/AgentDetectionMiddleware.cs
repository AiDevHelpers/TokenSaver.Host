using TokenSaver.Config;
using TokenSaver.Library.Configuration;
using TokenSaver.MCP.Services;

using Microsoft.Extensions.Options;

using System.Text;
using System.Text.Json.Nodes;

namespace TokenSaver.MCP.Middleware;

/// <summary>
///     Detects the calling AI model family on the MCP <c>initialize</c> handshake and, separately,
///     as a fallback on the first <c>tools/call</c> of a connection that skipped/lacked a proper
///     handshake (some clients go straight to tools/call in stateless mode). Logs what was
///     detected and which <c>agentConfig</c> settings now apply, so "the server picked
///     Anthropic/1h caching" is visible in logs without needing to call <c>ManageConfig</c>.
///     This is intentionally the closest thing to "session start" this server has: the MCP
///     transport here is stateless HTTP with no session store (see
///     <c>ContextInterceptMiddleware</c>'s doc comment), so <c>initialize</c> is a per-connection
///     signal, not a per-process one — detection re-runs on every connection's handshake rather
///     than being cached process-wide, since a long-lived server can field requests from more than
///     one client/model over its lifetime.
///     Does not mutate the request; purely observational/logging today. Tools that want the
///     resolved family/settings for the *current* request call
///     <see cref="AgentConfigResolver.Resolve" /> directly (see AutoWorkflowTools/CodeHelperTools/
///     LocalModelHelperTools) — this middleware's value is the one-time "here's what we detected
///     for this connection" log line plus a place to extend later if per-connection caching is
///     ever worth adding.
/// </summary>
internal sealed partial class AgentDetectionMiddleware
{
    [LoggerMessage(Level = LogLevel.Information,
        Message = "Agent detected at session start: family={Family}, client={Client}, model={Model} — " +
                   "promptCaching={PromptCaching}, cacheTtl={CacheTtl}s")]
    private static partial void LogAgentDetected(
        ILogger logger, string family, string client, string model, bool? promptCaching, int? cacheTtl);

    private readonly RequestDelegate _next;
    private readonly ILogger<AgentDetectionMiddleware> _logger;

    // Same rationale as ContextInterceptMiddleware: cap in-memory buffering of the peeked body.
    private const long MaxPeekedBodyBytes = 1 * 1024 * 1024;

    public AgentDetectionMiddleware(RequestDelegate next, ILogger<AgentDetectionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext context,
        AgentConfigResolver resolver,
        InstructionOptimizerService instructionOptimizer,
        IOptions<TokenSaverConfig> tokenSaverOptions)
    {
        if (context.Request.Method != HttpMethods.Post ||
            !context.Request.Path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase) ||
            context.Request.ContentLength is > MaxPeekedBodyBytes)
        {
            await _next(context);
            return;
        }

        context.Request.EnableBuffering(bufferThreshold: (int)MaxPeekedBodyBytes);
        string? method = null;
        try
        {
            using StreamReader reader = new(context.Request.Body, Encoding.UTF8, leaveOpen: true);
            string body = await reader.ReadToEndAsync();
            context.Request.Body.Position = 0;
            method = JsonNode.Parse(body)?["method"]?.GetValue<string>();
        }
        catch
        {
            context.Request.Body.Position = 0;
        }

        // Only the initialize handshake — every subsequent tools/call on the same connection
        // reuses the family AutoWorkflowTools/CodeHelperTools/LocalModelHelperTools already
        // resolve per-call via AgentConfigResolver, so re-detecting here on every call would be
        // redundant work for no new information.
        if (string.Equals(method, "initialize", StringComparison.Ordinal))
        {
            try
            {
                string workspaceRoot = tokenSaverOptions.Value.WorkspaceRoot ?? "";
                if (!string.IsNullOrWhiteSpace(workspaceRoot))
                {
                    // Session connect: re-create .tokensaver/config.json (and the sibling ignore
                    // files) if they were deleted since the server started or since the last
                    // session connected. Cheap (File.Exists-guarded, no-op when everything is
                    // already present) and best-effort — an I/O failure here must never block the
                    // handshake, same rationale as the resolve/log step below.
                    WorkspaceConfigDefaults.EnsureDefaults(workspaceRoot, _logger);

                    // Same best-effort, cheap-when-nothing-changed rationale as EnsureDefaults
                    // above, but for a config.json that already exists — patches in any
                    // top-level section missing since the file was created by an older schema.
                    ConfigUpgradeService.UpgradeIfNeeded(workspaceRoot, _logger);

                    AgentConfigResolution resolution = resolver.Resolve(context, workspaceRoot);
                    if (_logger.IsEnabled(LogLevel.Information))
                    {
                        LogAgentDetected(
                            _logger,
                            resolution.Family,
                            resolution.Caller.ClientName,
                            resolution.Caller.ModelHint ?? "(none)",
                            resolution.Settings.EnablePromptCaching,
                            resolution.Settings.CacheTtlSeconds);
                    }

                    // Best-effort, config-gated (repoHelper.optimizeInstructions, off by default) —
                    // see InstructionOptimizerService's doc comment.
                    instructionOptimizer.EnsureSynced(workspaceRoot, resolution.Caller);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException)
            {
                // Detection/logging failure must never block the handshake itself.
                _logger.LogWarning(ex, "Agent detection at session start failed; continuing without it");
            }
        }

        await _next(context);
    }
}
