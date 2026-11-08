using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using TokenSaver.Common.Core.Contracts;
using TokenSaver.MCP.Services;

namespace TokenSaver.MCP.Middleware;

/// <summary>
/// Middleware that intercepts all requests to extract and log debug context.
/// Associates each request with an extension client, captures timing, and records events.
/// </summary>
internal sealed class DebuggerContextInterceptor
{
    private readonly ILogger<DebuggerContextInterceptor> _logger;
    private readonly RequestDelegate _next;

    public DebuggerContextInterceptor(RequestDelegate next, ILogger<DebuggerContextInterceptor> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IDebuggerEventCapture eventCapture,
        IClientDebugContextService clientContextService,
        ExtensionRegistryService extensionRegistry)
    {
        var startTime = Stopwatch.GetTimestamp();
        var requestPath = context.Request.Path.Value ?? "";

        // Extract extension info from headers
        var extensionType = context.Request.Headers.TryGetValue("X-Extension-Type", out var extType)
            ? extType.ToString()
            : null;

        var extensionVersion = context.Request.Headers.TryGetValue("X-Extension-Version", out var extVer)
            ? extVer.ToString()
            : null;

        var modelHint = context.Request.Headers.TryGetValue("X-Model-Hint", out var model)
            ? model.ToString()
            : null;

        // Get or create client context
        var clientId = context.Request.Headers.TryGetValue("X-Client-Id", out var cid)
            ? cid.ToString()
            : null;

        if (string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(extensionType))
        {
            clientId = clientContextService.RegisterClient(extensionType, extensionVersion);
            context.Response.Headers["X-Client-Id"] = clientId;
        }

        // Record activity if we have a client
        if (!string.IsNullOrEmpty(clientId))
        {
            clientContextService.RecordActivity(clientId);
        }

        // Log request start
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Debug request from {Extension} v{Version}: {Method} {Path}",
                extensionType ?? "unknown",
                extensionVersion ?? "unknown",
                context.Request.Method,
                requestPath);
        }

        try
        {
            // Continue pipeline
            await _next(context);

            // Calculate duration
            var elapsed = Stopwatch.GetElapsedTime(startTime);

            // Record successful request as metric
            eventCapture.RecordMetric(
                metricName: "http-request",
                value: elapsed.TotalMilliseconds,
                unit: "ms",
                toolName: GetToolNameFromPath(requestPath),
                clientName: extensionType);
        }
        catch (Exception ex)
        {
            // Record exception
            eventCapture.RecordException(
                exception: ex,
                toolName: GetToolNameFromPath(requestPath),
                clientName: extensionType);

            _logger.LogError(
                ex,
                "Exception during debug request from {Extension}: {Path}",
                extensionType ?? "unknown",
                requestPath);

            throw;
        }
    }

    private static string? GetToolNameFromPath(string path)
    {
        // Extract tool name from MCP tool endpoints
        if (path.Contains("/mcp", StringComparison.Ordinal) && path.Contains("tool", StringComparison.Ordinal))
        {
            var parts = path.Split('/');
            var toolIndex = Array.IndexOf(parts, "tool");
            if (toolIndex >= 0 && toolIndex + 1 < parts.Length)
            {
                return parts[toolIndex + 1];
            }
        }

        // Extract API operation
        if (path.StartsWith("/api/debugger/", StringComparison.Ordinal))
        {
            return path.Replace("/api/debugger/", "", StringComparison.Ordinal).Split('/')[0];
        }

        return null;
    }
}

/// <summary>Extension methods for registering the debugger context interceptor.</summary>
internal static class DebuggerContextInterceptorExtensions
{
    public static IApplicationBuilder UseDebuggerContextInterceptor(this IApplicationBuilder app)
    {
        return app.UseMiddleware<DebuggerContextInterceptor>();
    }
}
