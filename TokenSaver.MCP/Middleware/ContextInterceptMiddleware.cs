using TokenSaver.CodeHelper.AutoWorkflow;
using TokenSaver.CodeHelper.Configuration;
using TokenSaver.ContextHelper.Configuration;
using TokenSaver.ContextHelper.Models;
using TokenSaver.ContextHelper.Pipeline;
using TokenSaver.Common.Core.Configuration;
using TokenSaver.MCP.Services;

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
    private static partial void LogSimpleModelServerHandled(ILogger logger, string tool, string? summary);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Context cleaning pipeline failed for tool {Tool} — passing through")]
    private static partial void LogContextCleaningFailed(ILogger logger, Exception ex, string tool);

    [LoggerMessage(Level = LogLevel.Information, Message = "Draft model deferred for tool {Tool}. Prompt augmented with guidance request.")]
    private static partial void LogSimpleModelServerDeferred(ILogger logger, string tool);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Draft model interceptor failed for tool {Tool} — passing through")]
    private static partial void LogSimpleModelServerInterceptorFailed(ILogger logger, Exception ex, string tool);

    [LoggerMessage(Level = LogLevel.Information, Message = "Injected task-type-matched instructions for tool {Tool} (taskType={TaskType})")]
    private static partial void LogInjectedInstructions(ILogger logger, string tool, TaskType taskType);

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

    private static string[]? ParseHistory(JsonObject? args)
    {
        if (args?["history"] is not JsonArray historyNode)
        {
            return null;
        }

        return historyNode
            .Select(n => n?.GetValue<string>())
            .Where(s => s != null)
            .ToArray()!;
    }

    private static async Task<string?> ReadBoundedBodyAsync(HttpRequest request)
    {
        request.EnableBuffering(bufferThreshold: (int)MaxInterceptedBodyBytes);
        using StreamReader reader = new(request.Body, Encoding.UTF8, leaveOpen: true);
        char[] buffer = new char[81_920];
        StringBuilder bodyBuilder = new();
        int charsRead;
        while ((charsRead = await reader.ReadAsync(buffer)) > 0)
        {
            _ = bodyBuilder.Append(buffer, 0, charsRead);
            if (bodyBuilder.Length > MaxInterceptedBodyBytes)
            {
                request.Body.Position = 0;
                return null;
            }
        }

        request.Body.Position = 0;
        return bodyBuilder.ToString();
    }

    public async Task InvokeAsync(
        HttpContext context,
        IOptions<CodeHelperConfig> codeHelperOpts,
        IOptions<ContextHelperConfig> contextHelperOpts,
        ContextCleanerService cleaner,
        SimpleModelServerInterceptorService simpleModelServerInterceptor,
        InstructionOptimizerService instructionOptimizer)
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

        // Buffer the request body so we can read and potentially replace it. Bails out (null) when
        // the body exceeds the cap mid-stream, rather than truncating a JSON-RPC payload.
        string? body = await ReadBoundedBodyAsync(context.Request);
        if (body is null)
        {
            await _next(context);
            return;
        }

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

        // Anything that is not a well-formed JSON-RPC object passes through untouched; the MCP
        // transport reports the protocol error. Indexing a non-object or reading a non-string
        // with GetValue<string> would otherwise throw out of the middleware.
        if (rpc is not JsonObject rpcObject)
        {
            await _next(context);
            return;
        }

        // Only intercept tools/call
        if (AsString(rpcObject["method"]) != "tools/call")
        {
            await _next(context);
            return;
        }

        JsonObject? rpcParams = rpcObject["params"] as JsonObject;
        string? toolName = AsString(rpcParams?["name"]);
        if (toolName == null || _skipTools.Contains(toolName))
        {
            await _next(context);
            return;
        }

        JsonObject? args = rpcParams?["arguments"] as JsonObject;
        string? prompt = AsString(args?["prompt"]);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            await _next(context);
            return;
        }

        string workspaceRoot = context.RequestServices
                .GetService(typeof(IOptions<TokenSaverConfig>))
            is IOptions<TokenSaverConfig> libOpts
            ? libOpts.Value.WorkspaceRoot ?? ""
            : "";

        // Step 1: Run context cleaning pipeline (gated on autoIntercept, independent of Step 2)
        string cleanedPrompt = await CleanPromptAsync(context, cleaner, instructionOptimizer, prompt, args, toolName, workspaceRoot, autoIntercept);
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

        if (await TryDraftInterceptAsync(context, simpleModelServerInterceptor, cleanedPrompt, workspaceRoot, toolName, args!, rpc))
        {
            return;
        }

        await _next(context);
    }

    /// <summary>Runs the draft-model interceptor. Returns true when it fully answered the request (response already written).</summary>
    private async Task<bool> TryDraftInterceptAsync(
        HttpContext context,
        SimpleModelServerInterceptorService simpleModelServerInterceptor,
        string cleanedPrompt,
        string workspaceRoot,
        string toolName,
        JsonObject args,
        JsonNode rpc)
    {
        try
        {
            SimpleModelServerInterceptionResult interceptResult = await simpleModelServerInterceptor.TryInterceptAsync(
                cleanedPrompt, workspaceRoot, context.RequestAborted);

            if (interceptResult.HandledBySimpleModelServer)
            {
                // Draft model implemented the task — return the result directly
                LogSimpleModelServerHandled(_logger, toolName, interceptResult.Summary);

                await WriteJsonRpcResultAsync(context, interceptResult, rpc["id"]?.DeepClone());
                return true;
            }

            if (interceptResult.IsDeferred)
            {
                // Draft model needs AI help — augment the prompt with the deferral message
                string deferredPrompt = interceptResult.Summary + "\n\n---\n\n" + cleanedPrompt;

                args["prompt"] = JsonValue.Create(deferredPrompt);
                args["_simpleModelServerIntercepted"] = JsonValue.Create(true);
                if (interceptResult.NeedsArchitectureHelp)
                {
                    args["_needsArchitectureHelp"] = JsonValue.Create(true);
                }

                string newBody = rpc.ToJsonString();
                byte[] newBytes = Encoding.UTF8.GetBytes(newBody);
                context.Request.Body = new MemoryStream(newBytes);
                context.Request.ContentLength = newBytes.Length;

                LogSimpleModelServerDeferred(_logger, toolName);
            }
        }
        catch (Exception ex)
        {
            // Interceptor failure — don't block the tool call
            LogSimpleModelServerInterceptorFailed(_logger, ex, toolName);
        }

        return false;
    }

    /// <summary>Returns the node's string value, or null when it is missing or not a JSON string.</summary>
    internal static string? AsString(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue(out string? text) ? text : null;
    }

    private static async Task WriteJsonRpcResultAsync(HttpContext context, SimpleModelServerInterceptionResult result, JsonNode? requestId)
    {
        // Build a JSON-RPC success response. The id must echo the request's id or the client
        // cannot correlate the response.
        JsonObject response = new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = requestId,
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

    /// <summary>Runs the context-cleaning pipeline and draft-doc injection; returns the prompt to forward.</summary>
    private async Task<string> CleanPromptAsync(
        HttpContext context,
        ContextCleanerService cleaner,
        InstructionOptimizerService instructionOptimizer,
        string prompt,
        JsonObject? args,
        string toolName,
        string workspaceRoot,
        bool autoIntercept)
    {
        string cleanedPrompt = prompt;
        if (autoIntercept)
        {
            try
            {
                // Extract optional history array
                string[]? history = ParseHistory(args);

                PipelineResult ctxResult = await cleaner.CleanContextAsync(prompt, history);
                cleanedPrompt = ctxResult.ContextText;

                // Inject a task-type-matched sub-doc (e.g. "when debugging") directly, rather than
                // relying on the connecting client's own lazy-loading — path-glob-triggered rule
                // files never fire for a purely conversational request that doesn't touch a
                // matching file, even when the task itself clearly needs that guidance.
                TaskType? taskType = ctxResult.Summary?.TaskType;
                if (taskType is not null && !string.IsNullOrWhiteSpace(workspaceRoot))
                {
                    string? injectedDoc = instructionOptimizer.TryInjectRelevantDoc(workspaceRoot, taskType.Value);
                    if (!string.IsNullOrWhiteSpace(injectedDoc))
                    {
                        cleanedPrompt = $"<!-- tokensaver:injected-instructions ({taskType}) -->\n{injectedDoc.Trim()}\n<!-- /tokensaver:injected-instructions -->\n\n{cleanedPrompt}";
                        LogInjectedInstructions(_logger, toolName, taskType.Value);
                    }
                }

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
        return cleanedPrompt;
    }

}
