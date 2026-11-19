using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using TokenSaver.Common.Core.Contracts;

namespace TokenSaver.MCP.Middleware;

/// <summary>
/// Middleware that intercepts all requests to extract and log debug context.
/// Captures request timing and records debugger telemetry events.
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
        IDebuggerEventCapture eventCapture)
    {
        var startTime = Stopwatch.GetTimestamp();
        var requestPath = context.Request.Path.Value ?? "";

        // Extract extension info from headers
        var extensionType = context.Request.Headers.TryGetValue("X-Extension-Type", out var extType)
            ? extType.ToString()
            : null;

        string? clientName = extensionType;

        // Log request start
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Debug request from {Extension}: {Method} {Path}",
                extensionType ?? "unknown",
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
                clientName: clientName);
        }
        catch (Exception ex)
        {
            // Record exception
            eventCapture.RecordException(
                exception: ex,
                toolName: GetToolNameFromPath(requestPath),
                clientName: clientName);

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
