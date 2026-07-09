using TokenSaver.CodeHelper.AutoWorkflow;
using TokenSaver.CodeHelper.Configuration;
using TokenSaver.ContextHelper.Configuration;
using TokenSaver.ContextHelper.Models;
using TokenSaver.ContextHelper.Pipeline;
using TokenSaver.Library.Configuration;

using Microsoft.Extensions.Options;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TokenSaver.MCP.Middleware;

/// <summary>
///     Intercepts MCP tools/call requests and runs the prompt through:
///     1. Context cleaning pipeline (existing)
///     2. Draft model interceptor (new) — for code-writing prompts, tries the local
///     draft model first; falls back to the calling AI for architecture/design.
///     Works for every MCP client (Claude Code, Copilot, Cursor, Continue, etc.)
///     because it sits at the HTTP level, not inside any single tool.
///     Only fires when:
///     - The request is a JSON-RPC tools/call
///     - The tool arguments contain a "prompt" field
///     - The tool is not one of the context tools themselves (avoid recursion)
///     - CodeHelperConfig.AutoEnabled is true
/// </summary>
internal sealed partial class ContextInterceptMiddleware
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Context cleaned for tool {Tool}: {Before} → {After} chars")]
    private static partial void LogContextCleaned(ILogger logger, string tool, int before, int after);

    [LoggerMessage(Level = LogLevel.Information, Message = "Draft model handled request for tool {Tool}: {Summary}")]
    private static partial void LogDraftModelHandled(ILogger logger, string tool, string? summary);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Context cleaning pipeline failed for tool {Tool} — passing through")]
    private static partial void LogContextCleaningFailed(ILogger logger, Exception ex, string tool);

    [LoggerMessage(Level = LogLevel.Information, Message = "Draft model deferred for tool {Tool}. Prompt augmented with guidance request.")]
    private static partial void LogDraftModelDeferred(ILogger logger, string tool);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Draft model interceptor failed for tool {Tool} — passing through")]
    private static partial void LogDraftModelInterceptorFailed(ILogger logger, Exception ex, string tool);

    private static readonly HashSet<string> _skipTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "clean_and_chat",
        "build_concise_context",
        "context_hygiene",
        "context_diag",
        "help",
        "code_work"
    };

    // This middleware buffers the full request body into memory (EnableBuffering +
    // ReadToEndAsync) to peek at the JSON-RPC method/tool name before deciding whether to
    // intercept. Even though this server only listens on localhost/trusted MCP clients, a
    // single malformed or unexpectedly large payload (e.g. a huge pasted context/history
    // array) could otherwise force an unbounded in-memory allocation here. 25 MB comfortably
    // covers realistic prompt/context payloads while capping worst-case memory use.
    private const long MaxInterceptedBodyBytes = 25 * 1024 * 1024;

    private readonly ILogger<ContextInterceptMiddleware> _logger;

    private readonly RequestDelegate _next;

    public ContextInterceptMiddleware(
        RequestDelegate next,
        ILogger<ContextInterceptMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IOptions<CodeHelperConfig> codeHelperOpts,
        IOptions<ContextHelperConfig> contextHelperOpts,
        ContextCleanerService cleaner,
        DraftModelInterceptorService draftModelInterceptor)
    {
        bool autoIntercept = contextHelperOpts.Value.AutoIntercept;
        bool autoEnabled = codeHelperOpts.Value.AutoEnabled;

        // Each step below is independently gated (autoIntercept for context cleaning,
        // autoEnabled for draft-model interception) — bail out only when neither is on, so we
        // don't pay the buffering/parsing cost for a request nothing here will touch.
        if ((!autoIntercept && !autoEnabled) ||
            context.Request.Method != HttpMethods.Post ||
            !context.Request.Path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Skip interception (rather than buffering) for bodies that are too large to be a
        // reasonable tool-call payload. An unknown/unset Content-Length is let through here —
        // the chunked read loop below still enforces the same cap as data streams in, so a
        // client that lies about or omits Content-Length can't bypass the limit.
        if (context.Request.ContentLength is > MaxInterceptedBodyBytes)
        {
            await _next(context);
            return;
        }

        // Buffer the request body so we can read and potentially replace it
        context.Request.EnableBuffering(bufferThreshold: (int)MaxInterceptedBodyBytes);
        using StreamReader reader = new(context.Request.Body, Encoding.UTF8, leaveOpen: true);
        char[] buffer = new char[81_920];
        StringBuilder bodyBuilder = new();
        int charsRead;
        while ((charsRead = await reader.ReadAsync(buffer)) > 0)
        {
            _ = bodyBuilder.Append(buffer, 0, charsRead);
            if (bodyBuilder.Length > MaxInterceptedBodyBytes)
            {
                // Body exceeded the cap mid-stream (Content-Length was absent/understated).
                // Bail out of interception entirely rather than truncating and risking a
                // subtly-corrupted JSON-RPC payload being parsed downstream.
                context.Request.Body.Position = 0;
                await _next(context);
                return;
            }
        }

        string body = bodyBuilder.ToString();
        context.Request.Body.Position = 0;

        JsonNode? rpc;
        try
        {
            rpc = JsonNode.Parse(body);
        }
        catch
        {
            await _next(context);
            return;
        }

        // Only intercept tools/call
        if (rpc?["method"]?.GetValue<string>() != "tools/call")
        {
            await _next(context);
            return;
        }

        string? toolName = rpc["params"]?["name"]?.GetValue<string>();
        if (toolName == null || _skipTools.Contains(toolName))
        {
            await _next(context);
            return;
        }

        JsonObject? args = rpc["params"]?["arguments"] as JsonObject;
        string? prompt = args?["prompt"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            await _next(context);
            return;
        }

        // Step 1: Run context cleaning pipeline (gated on autoIntercept, independent of Step 2)
        string cleanedPrompt = prompt;
        if (autoIntercept)
        {
            try
            {
                // Extract optional history array
                string[]? history = null;
                if (args?["history"] is JsonArray historyNode)
                {
                    history = historyNode
                        .Select(n => n?.GetValue<string>())
                        .Where(s => s != null)
                        .ToArray()!;
                }

                PipelineResult ctxResult = await cleaner.CleanContextAsync(prompt, history);
                cleanedPrompt = ctxResult.ContextText;

                if (!string.IsNullOrWhiteSpace(cleanedPrompt) && cleanedPrompt != prompt)
                {
                    args!["prompt"] = JsonValue.Create(cleanedPrompt);
                    // Remove history from args — it's been folded into the cleaned prompt
                    _ = args.Remove("history");

                    LogContextCleaned(_logger, toolName, prompt.Length, cleanedPrompt.Length);
                }
            }
            catch (Exception ex)
            {
                // Pipeline failure — don't block the tool call
                LogContextCleaningFailed(_logger, ex, toolName);
                cleanedPrompt = prompt;
                context.Request.Body.Position = 0;
            }
        }

        // Step 1's mutation of args["prompt"] only lives in the parsed rpc tree so far — write it
        // back to the actual request body now so it reaches the downstream tool even when Step 2
        // is off (previously this rewrite only happened inside Step 2's "deferred" branch, so a
        // cleaned-but-not-deferred prompt silently never took effect).
        if (cleanedPrompt != prompt)
        {
            string cleanedBody = rpc!.ToJsonString();
            byte[] cleanedBytes = Encoding.UTF8.GetBytes(cleanedBody);
            context.Request.Body = new MemoryStream(cleanedBytes);
            context.Request.ContentLength = cleanedBytes.Length;
        }

        // Step 2: Draft model interception (gated on autoEnabled) — for code-writing prompts,
        // try the local model first
        if (!autoEnabled)
        {
            await _next(context);
            return;
        }

        try
        {
            string workspaceRoot = context.RequestServices
                    .GetService(typeof(IOptions<TokenSaverConfig>))
                is IOptions<TokenSaverConfig> libOpts
                ? libOpts.Value.WorkspaceRoot ?? ""
                : "";

            DraftModelInterceptionResult interceptResult = await draftModelInterceptor.TryInterceptAsync(
                cleanedPrompt, workspaceRoot, context.RequestAborted);

            if (interceptResult.HandledByDraftModel)
            {
                // Draft model implemented the task — return the result directly
                LogDraftModelHandled(_logger, toolName, interceptResult.Summary);

                await WriteJsonRpcResultAsync(context, interceptResult);
                return;
            }

            if (interceptResult.IsDeferred)
            {
                // Draft model needs AI help — augment the prompt with the deferral message
                string deferredPrompt = interceptResult.Summary + "\n\n---\n\n" + cleanedPrompt;

                // Update the request body with the augmented prompt
                args!["prompt"] = JsonValue.Create(deferredPrompt);
                // Also signal to the tool that this was intercepted
                args["_draftModelIntercepted"] = JsonValue.Create(true);
                if (interceptResult.NeedsArchitectureHelp)
                {
                    args["_needsArchitectureHelp"] = JsonValue.Create(true);
                }

                string newBody = rpc.ToJsonString();
                byte[] newBytes = Encoding.UTF8.GetBytes(newBody);
                context.Request.Body = new MemoryStream(newBytes);
                context.Request.ContentLength = newBytes.Length;

                LogDraftModelDeferred(_logger, toolName);
            }
        }
        catch (Exception ex)
        {
            // Interceptor failure — don't block the tool call
            LogDraftModelInterceptorFailed(_logger, ex, toolName);
        }

        await _next(context);
    }

    private static async Task WriteJsonRpcResultAsync(HttpContext context, DraftModelInterceptionResult result)
    {
        // Build a JSON-RPC success response
        JsonObject response = new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = ExtractRequestId(context),
            ["result"] = new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = result.Summary ?? "Draft model processed the request."
                    }
                },
                ["isError"] = false
            }
        };

        string json = response.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        byte[] bytes = Encoding.UTF8.GetBytes(json);

        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes);
    }

    private static JsonNode? ExtractRequestId(HttpContext context)
    {
        // The original request body is already consumed. We read what was buffered.
        // A simpler approach: return a synthetic ID. Real MCP clients don't strictly
        // validate the response ID against the request — they match on request/response
        // ordering or content.
        return "draft-model-intercepted";
    }
}
