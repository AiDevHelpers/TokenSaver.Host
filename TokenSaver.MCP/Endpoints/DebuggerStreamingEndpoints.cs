using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using TokenSaver.Common.Core.Contracts;
using TokenSaver.MCP.Services;

namespace TokenSaver.MCP.Endpoints;

/// <summary>
/// HTTP endpoints for streaming debug events and problems as Server-Sent Events (SSE)
/// and for querying debug data as JSON.
/// </summary>
internal static class DebuggerStreamingEndpoints
{
    public static void MapDebuggerStreamingEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/debugger/stream")
            .WithTags("Debugger Streaming");

        group.MapGet("/events", StreamDebugEvents)
            .WithName("StreamDebugEvents");

        group.MapGet("/problems", StreamDebugProblems)
            .WithName("StreamDebugProblems");

        // Metrics and stats endpoints
        var metricsGroup = app.MapGroup("/api/debugger/metrics")
            .WithTags("Debugger Metrics");

        metricsGroup.MapGet("/summary", GetMetricsSummary)
            .WithName("GetMetricsSummary");

        metricsGroup.MapGet("/stream-stats", GetStreamStats)
            .WithName("GetStreamStats");

        metricsGroup.MapGet("/subscriptions", GetSubscriptionDetails)
            .WithName("GetSubscriptionDetails");
    }

    private static async Task StreamDebugEvents(
        HttpContext context,
        [FromServices] IDebuggerEventCapture eventCapture,
        [FromServices] DebuggerEventStreamManager streamManager,
        [FromQuery] string? eventType = null,
        [FromQuery] string? severity = null,
        [FromQuery] string? clientName = null,
        [FromQuery] string? toolName = null)
    {
        // Set SSE headers
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers["Cache-Control"] = "no-cache";
        context.Response.Headers["Connection"] = "keep-alive";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        // Create filter function
        bool EventFilter(DebuggerEvent evt)
        {
            if (!string.IsNullOrEmpty(eventType) &&
                !evt.EventType.ToString().Equals(eventType, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrEmpty(severity) &&
                !evt.Severity.ToString().Equals(severity, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrEmpty(clientName) &&
                !string.Equals(evt.CallerInfo.ClientName, clientName, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrEmpty(toolName) &&
                !string.Equals(evt.ToolName, toolName, StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        // Add subscription
        string subscriptionId = streamManager.AddSubscription(
            onEventAsync: async evt =>
            {
                var json = JsonSerializer.Serialize(new
                {
                    id = evt.Id.ToString("N"),
                    timestamp = evt.Timestamp,
                    type = evt.EventType.ToString(),
                    severity = evt.Severity.ToString(),
                    toolName = evt.ToolName,
                    duration = evt.Duration?.TotalMilliseconds,
                    clientName = evt.CallerInfo.ClientName,
                    data = evt.Data
                });

                await context.Response.WriteAsync($"data: {json}\n\n");
                await context.Response.Body.FlushAsync();
            },
            filter: EventFilter,
            subscriptionName: $"EventStream-{clientName ?? "all"}");

        try
        {
            // Keep the connection open until client disconnects
            await Task.Delay(Timeout.Infinite, context.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected, this is expected
        }
        finally
        {
            streamManager.RemoveSubscription(subscriptionId);
        }
    }

    private static async Task StreamDebugProblems(
        HttpContext context,
        [FromServices] IDebuggerProblemCapture problemCapture,
        [FromServices] DebuggerEventStreamManager streamManager,
        [FromQuery] string? problemType = null,
        [FromQuery] string? severity = null,
        [FromQuery] string? filePath = null)
    {
        // Set SSE headers
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers["Cache-Control"] = "no-cache";
        context.Response.Headers["Connection"] = "keep-alive";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        // For problems, we'll poll and send periodic updates
        string subscriptionId = streamManager.AddSubscription(
            onEventAsync: async evt =>
            {
                // Only send problem-related events (exceptions that are problems)
                if (evt.EventType != DebuggerEventType.Exception)
                    return;

                var json = JsonSerializer.Serialize(new
                {
                    type = "problem-detected",
                    timestamp = evt.Timestamp,
                    details = evt.Data
                });

                await context.Response.WriteAsync($"data: {json}\n\n");
                await context.Response.Body.FlushAsync();
            },
            filter: evt => evt.EventType == DebuggerEventType.Exception,
            subscriptionName: $"ProblemStream-{filePath ?? "all"}");

        try
        {
            // Send periodic problem summary
            var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var stats = problemCapture.GetProblemStats();

                    var summary = new
                    {
                        type = "problem-summary",
                        timestamp = DateTime.UtcNow,
                        totalProblems = stats.TotalProblemCount,
                        errors = stats.ErrorCount,
                        warnings = stats.WarningCount,
                        affectedFiles = stats.AffectedFiles.Count
                    };

                    var json = JsonSerializer.Serialize(summary);
                    await context.Response.WriteAsync($"data: {json}\n\n");
                    await context.Response.Body.FlushAsync();

                    await Task.Delay(5000, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            streamManager.RemoveSubscription(subscriptionId);
        }
    }

    private static IResult GetMetricsSummary(
        [FromServices] IDebuggerEventCapture eventCapture,
        [FromServices] IDebuggerProblemCapture problemCapture)
    {
        var eventStats = eventCapture.GetEventStats();
        var problemStats = problemCapture.GetProblemStats();

        var result = new
        {
            timestamp = DateTime.UtcNow,
            events = new
            {
                total = eventStats.TotalEventCount,
                byType = eventStats.EventCountByType,
                bySeverity = eventStats.EventCountBySeverity,
                byClient = eventStats.EventCountByClient
            },
            problems = new
            {
                total = problemStats.TotalProblemCount,
                errors = problemStats.ErrorCount,
                warnings = problemStats.WarningCount,
                affectedFiles = problemStats.AffectedFiles.Count,
                byType = problemStats.ProblemCountByType
            }
        };

        return Results.Ok(result);
    }

    private static IResult GetStreamStats(
        [FromServices] DebuggerEventStreamManager streamManager)
    {
        var stats = streamManager.GetStreamStats();

        var result = new
        {
            timestamp = DateTime.UtcNow,
            subscriptions = new
            {
                total = stats.TotalSubscriptions,
                active = stats.ActiveSubscriptions,
                idle = stats.IdleSubscriptions
            },
            events = new
            {
                totalSent = stats.TotalEventsSent,
                oldestSubscriptionAt = stats.OldestSubscription,
                mostRecentEventAt = stats.MostRecentEvent
            }
        };

        return Results.Ok(result);
    }

    private static IResult GetSubscriptionDetails(
        [FromServices] DebuggerEventStreamManager streamManager)
    {
        var subscriptions = streamManager.GetSubscriptionDetails();

        var result = new
        {
            timestamp = DateTime.UtcNow,
            subscriptionCount = subscriptions.Count,
            subscriptions = subscriptions.Select(s => new
            {
                id = s.SubscriptionId,
                name = s.SubscriptionName,
                createdAt = s.CreatedAt,
                lastEventAt = s.LastEventSentAt,
                eventCount = s.EventsCount,
                hasFilter = s.HasFilter,
                isActive = s.IsActive
            }).ToList()
        };

        return Results.Ok(result);
    }
}
