using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using TokenSaver.Common.Core.Contracts;
using TokenSaver.MCP.Services;

namespace TokenSaver.MCP.Tools;

/// <summary>
/// MCP tools for querying captured errors, problems, and debug events as JSON.
/// Used for code analysis, error tracking, and debugging.
/// </summary>
[McpServerToolType]
internal sealed class DebuggerAnalysisTools
{
    private readonly IDebuggerEventCapture _eventCapture;
    private readonly IDebuggerProblemCapture _problemCapture;

    public DebuggerAnalysisTools(
        IDebuggerEventCapture eventCapture,
        IDebuggerProblemCapture problemCapture)
    {
        _eventCapture = eventCapture;
        _problemCapture = problemCapture;
    }

    /// <summary>
    /// Get all captured errors and problems as JSON for analysis.
    /// Useful for code analysis, linting reports, and error tracking.
    /// </summary>
    [McpServerTool]
    [Description("Get all captured errors and problems as JSON for code analysis and error tracking")]
    public string GetCapturedProblems(
        [Description("Problem type filter (e.g., CompilationError, RuntimeError, Warning, CodeSmell, PerformanceIssue, SecurityIssue)")] string? problemType = null,
        [Description("Minimum severity to include: Info, Warning, Error, Fatal")] string? severity = null,
        [Description("File path to filter by")] string? filePath = null,
        [Description("Tool name that detected problems")] string? toolName = null,
        [Description("Maximum number of problems to return")] int? limitCount = null,
        [Description("Include file breakdown in statistics")] bool includeFileBreakdown = true)
    {
        DebugSeverity? severityEnum = null;
        if (severity != null)
        {
            severityEnum = Enum.Parse<DebugSeverity>(severity, ignoreCase: true);
        }

        var problems = _problemCapture.QueryProblems(
            problemType: problemType,
            severity: severityEnum,
            filePath: filePath,
            toolName: toolName,
            limitCount: limitCount ?? 1000);

        var stats = _problemCapture.GetProblemStats();
        var analysis = DebuggerProblemsAnalysisJson.FromStats(stats, problems, includeFileBreakdown);

        return JsonSerializer.Serialize(analysis, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Get problems grouped by file for code analysis and file-level reporting.
    /// </summary>
    [McpServerTool]
    [Description("Get problems grouped by file for code analysis and file-level reporting")]
    public string GetProblemsByFile(
        [Description("Problem type filter (optional)")] string? problemType = null)
    {
        var byFile = _problemCapture.GetProblemsByFile(filterProblemType: problemType);
        var result = new
        {
            timestamp = DateTime.UtcNow,
            files = byFile.Select(kvp => new
            {
                filePath = kvp.Key,
                problemCount = kvp.Value.Count,
                problems = kvp.Value.Select(DebuggerProblemJson.FromProblem).ToList(),
                criticalProblems = kvp.Value.Count(p => p.Severity == DebugSeverity.Error || p.Severity == DebugSeverity.Fatal),
                warnings = kvp.Value.Count(p => p.Severity == DebugSeverity.Warning)
            }).OrderByDescending(f => f.problemCount).ToList()
        };

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Get problems grouped by severity for prioritization.
    /// </summary>
    [McpServerTool]
    [Description("Get problems grouped by severity for prioritization")]
    public string GetProblemsBySeverity(
        [Description("Problem type filter (optional)")] string? problemType = null)
    {
        var bySeverity = _problemCapture.GetProblemsBySeverity(filterProblemType: problemType);
        var result = new
        {
            timestamp = DateTime.UtcNow,
            bySeverity = bySeverity.ToDictionary(
                kvp => kvp.Key.ToString(),
                kvp => new
                {
                    count = kvp.Value.Count,
                    problems = kvp.Value.Select(DebuggerProblemJson.FromProblem).ToList()
                })
        };

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Get a summary of captured problems for dashboard/monitoring.
    /// </summary>
    [McpServerTool]
    [Description("Get a summary of captured problems for dashboard and monitoring")]
    public string GetProblemSummary()
    {
        var stats = _problemCapture.GetProblemStats();
        var result = new
        {
            timestamp = DateTime.UtcNow,
            summary = new
            {
                totalProblems = stats.TotalProblemCount,
                errors = stats.ErrorCount,
                warnings = stats.WarningCount,
                affectedFiles = stats.AffectedFiles.Count,
                affectedFilesList = stats.AffectedFiles.Take(10).ToList(),
                problemsByType = stats.ProblemCountByType,
                oldestProblem = stats.OldestProblemTime,
                newestProblem = stats.NewestProblemTime
            }
        };

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Get captured debug events as JSON for analysis (tool calls, exceptions, state changes).
    /// </summary>
    [McpServerTool]
    [Description("Get captured debug events as JSON for analysis")]
    public string GetDebugEvents(
        [Description("Event type: ToolExecution, Exception, StateChange, Metric, SessionEvent, Other")] string? eventType = null,
        [Description("Minimum severity: Info, Warning, Error, Fatal")] string? severity = null,
        [Description("Filter by client name")] string? clientName = null,
        [Description("Filter by tool name")] string? toolName = null,
        [Description("Maximum number of events to return")] int? limitCount = null)
    {
        DebuggerEventType? eventTypeEnum = null;
        if (eventType != null)
        {
            eventTypeEnum = Enum.Parse<DebuggerEventType>(eventType, ignoreCase: true);
        }

        DebugSeverity? severityEnum = null;
        if (severity != null)
        {
            severityEnum = Enum.Parse<DebugSeverity>(severity, ignoreCase: true);
        }

        var events = _eventCapture.QueryEvents(
            eventType: eventTypeEnum,
            severity: severityEnum,
            clientName: clientName,
            toolName: toolName,
            limitCount: limitCount ?? 1000);

        var stats = _eventCapture.GetEventStats();
        var result = new
        {
            timestamp = DateTime.UtcNow,
            stats = new
            {
                totalEvents = stats.TotalEventCount,
                byType = stats.EventCountByType,
                bySeverity = stats.EventCountBySeverity,
                byClient = stats.EventCountByClient,
                oldestEvent = stats.OldestEventTime,
                newestEvent = stats.NewestEventTime
            },
            events = events.Select(e => new
            {
                id = e.Id.ToString("N"),
                timestamp = e.Timestamp,
                type = e.EventType.ToString(),
                severity = e.Severity.ToString(),
                toolName = e.ToolName,
                duration = e.Duration?.TotalMilliseconds,
                clientName = e.CallerInfo.ClientName,
                clientVersion = e.CallerInfo.ClientVersion,
                modelHint = e.CallerInfo.ModelHint,
                data = e.Data,
                stackTrace = e.StackTrace
            }).ToList()
        };

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Get high-priority issues (errors and critical warnings) for quick assessment.
    /// </summary>
    [McpServerTool]
    [Description("Get high-priority issues (errors and critical warnings) for quick assessment")]
    public string GetHighPriorityIssues()
    {
        var problems = _problemCapture.QueryProblems(
            severity: DebugSeverity.Error,
            limitCount: 100);

        var errors = problems.Where(p => p.Severity == DebugSeverity.Error).ToList();
        var fatals = problems.Where(p => p.Severity == DebugSeverity.Fatal).ToList();

        var result = new
        {
            timestamp = DateTime.UtcNow,
            critical = new
            {
                fatalCount = fatals.Count,
                errorCount = errors.Count,
                totalCritical = fatals.Count + errors.Count
            },
            issues = fatals.Concat(errors)
                .OrderByDescending(p => p.Severity)
                .ThenByDescending(p => p.Timestamp)
                .Take(50)
                .Select(DebuggerProblemJson.FromProblem)
                .ToList()
        };

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Clear all captured problems and events (for fresh analysis session).
    /// </summary>
    [McpServerTool]
    [Description("Clear all captured problems and events for a fresh analysis session")]
    public string ClearAllDiagnostics()
    {
        _problemCapture.Clear();
        _eventCapture.Clear();

        return JsonSerializer.Serialize(new
        {
            timestamp = DateTime.UtcNow,
            message = "All captured problems and events have been cleared"
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}
