using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using TokenSaver.Common.Core.Contracts;

namespace TokenSaver.MCP.Services;

/// <summary>
/// Captures and analyzes problems (errors, warnings, code issues) for code analysis,
/// debugging, and error tracking. Supports JSON serialization for tool consumption.
/// </summary>
internal sealed class DebuggerProblemService : IDebuggerProblemCapture
{
    private readonly ILogger<DebuggerProblemService> _logger;
    private readonly int _maxProblemsInMemory;
    private readonly ConcurrentQueue<DebuggerProblem> _problemBuffer;
    private readonly object _capacityLock = new();

    public DebuggerProblemService(ILogger<DebuggerProblemService> logger, int maxProblemsInMemory = 5000)
    {
        _logger = logger;
        _maxProblemsInMemory = maxProblemsInMemory;
        _problemBuffer = new ConcurrentQueue<DebuggerProblem>();
    }

    public void RecordProblem(
        string problemType,
        string title,
        string? filePath = null,
        int? lineNumber = null,
        int? columnNumber = null,
        string? details = null,
        string? suggestion = null,
        string? toolName = null,
        string? clientName = null,
        string? ruleId = null)
    {
        var severity = ClassifySeverity(problemType);
        var problem = new DebuggerProblem(
            Id: Guid.NewGuid(),
            Timestamp: DateTime.UtcNow,
            ProblemType: problemType,
            Severity: severity,
            Title: title,
            FilePath: filePath,
            LineNumber: lineNumber,
            ColumnNumber: columnNumber,
            Details: details,
            Suggestion: suggestion,
            ToolName: toolName,
            ClientName: clientName,
            RuleId: ruleId,
            IsResolved: false);

        EnqueueProblem(problem);

        _logger.LogWarning(
            "Problem recorded: {ProblemType} in {FilePath}:{LineNumber} - {Title} (Rule: {RuleId})",
            problemType,
            filePath ?? "unknown",
            lineNumber ?? 0,
            title,
            ruleId ?? "N/A");
    }

    public IReadOnlyList<DebuggerProblem> GetAllProblems(int? limitCount = null)
    {
        var all = _problemBuffer.ToList();
        if (limitCount.HasValue && limitCount > 0)
        {
            all = all.TakeLast(limitCount.Value).ToList();
        }
        return all.AsReadOnly();
    }

    public IReadOnlyList<DebuggerProblem> QueryProblems(
        string? problemType = null,
        DebugSeverity? severity = null,
        string? filePath = null,
        string? toolName = null,
        string? clientName = null,
        DateTime? startTime = null,
        DateTime? endTime = null,
        int? limitCount = null)
    {
        var filtered = _problemBuffer.Where(p =>
            (string.IsNullOrEmpty(problemType) || p.ProblemType == problemType)
            && (severity == null || p.Severity == severity)
            && (string.IsNullOrEmpty(filePath) || p.FilePath == filePath)
            && (string.IsNullOrEmpty(toolName) || p.ToolName == toolName)
            && (string.IsNullOrEmpty(clientName) || p.ClientName == clientName)
            && (startTime == null || p.Timestamp >= startTime)
            && (endTime == null || p.Timestamp <= endTime))
            .ToList();

        if (limitCount.HasValue && limitCount > 0)
        {
            filtered = filtered.TakeLast(limitCount.Value).ToList();
        }

        return filtered.AsReadOnly();
    }

    public IReadOnlyDictionary<string, IReadOnlyList<DebuggerProblem>> GetProblemsByFile(string? filterProblemType = null)
    {
        var filtered = _problemBuffer
            .Where(p => string.IsNullOrEmpty(filterProblemType) || p.ProblemType == filterProblemType)
            .Where(p => p.FilePath != null)
            .GroupBy(p => p.FilePath!)
            .ToDictionary(g => g.Key, g => g.ToList().AsReadOnly() as IReadOnlyList<DebuggerProblem>);

        return filtered.AsReadOnly();
    }

    public IReadOnlyDictionary<DebugSeverity, IReadOnlyList<DebuggerProblem>> GetProblemsBySeverity(string? filterProblemType = null)
    {
        var filtered = _problemBuffer
            .Where(p => string.IsNullOrEmpty(filterProblemType) || p.ProblemType == filterProblemType)
            .GroupBy(p => p.Severity)
            .ToDictionary(g => g.Key, g => g.ToList().AsReadOnly() as IReadOnlyList<DebuggerProblem>);

        return filtered.AsReadOnly();
    }

    public DebuggerProblemStats GetProblemStats()
    {
        var allProblems = _problemBuffer.ToList();
        var affectedFiles = allProblems
            .Where(p => p.FilePath != null)
            .Select(p => p.FilePath!)
            .Distinct()
            .ToList();

        return new DebuggerProblemStats(
            TotalProblemCount: allProblems.Count,
            ErrorCount: allProblems.Count(p => p.Severity == DebugSeverity.Error || p.Severity == DebugSeverity.Fatal),
            WarningCount: allProblems.Count(p => p.Severity == DebugSeverity.Warning),
            ProblemCountByType: allProblems.GroupBy(p => p.ProblemType)
                .ToDictionary(g => g.Key, g => g.Count()),
            ProblemCountByFile: allProblems
                .Where(p => p.FilePath != null)
                .GroupBy(p => p.FilePath!)
                .ToDictionary(g => g.Key, g => g.Count()),
            AffectedFiles: affectedFiles.AsReadOnly(),
            OldestProblemTime: allProblems.MinBy(p => p.Timestamp)?.Timestamp,
            NewestProblemTime: allProblems.MaxBy(p => p.Timestamp)?.Timestamp);
    }

    public void Clear()
    {
        while (_problemBuffer.TryDequeue(out _))
        {
        }
    }

    /// <summary>Classify severity based on problem type.</summary>
    private static DebugSeverity ClassifySeverity(string problemType)
    {
        return problemType.ToUpperInvariant() switch
        {
            var x when x.Contains("ERROR", StringComparison.Ordinal) || x.Contains("CRITICAL", StringComparison.Ordinal) => DebugSeverity.Error,
            var x when x.Contains("WARNING", StringComparison.Ordinal) => DebugSeverity.Warning,
            var x when x.Contains("INFO", StringComparison.Ordinal) => DebugSeverity.Info,
            var x when x.Contains("FATAL", StringComparison.Ordinal) || x.Contains("CRASH", StringComparison.Ordinal) => DebugSeverity.Fatal,
            _ => DebugSeverity.Warning
        };
    }

    private void EnqueueProblem(DebuggerProblem problem)
    {
        _problemBuffer.Enqueue(problem);

        lock (_capacityLock)
        {
            while (_problemBuffer.Count > _maxProblemsInMemory && _problemBuffer.TryDequeue(out _))
            {
            }
        }
    }
}

/// <summary>JSON-serializable view of a problem for external tools.</summary>
[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
internal sealed record DebuggerProblemJson(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("timestamp")] DateTime Timestamp,
    [property: JsonPropertyName("type")] string ProblemType,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("file")] string? FilePath,
    [property: JsonPropertyName("line")] int? LineNumber,
    [property: JsonPropertyName("column")] int? ColumnNumber,
    [property: JsonPropertyName("details")] string? Details,
    [property: JsonPropertyName("suggestion")] string? Suggestion,
    [property: JsonPropertyName("tool")] string? ToolName,
    [property: JsonPropertyName("client")] string? ClientName,
    [property: JsonPropertyName("ruleId")] string? RuleId,
    [property: JsonPropertyName("isResolved")] bool IsResolved = false)
{
    /// <summary>Convert a DebuggerProblem to JSON-serializable form.</summary>
    public static DebuggerProblemJson FromProblem(DebuggerProblem problem)
    {
        return new DebuggerProblemJson(
            Id: problem.Id.ToString("N"),
            Timestamp: problem.Timestamp,
            ProblemType: problem.ProblemType,
            Severity: problem.Severity.ToString(),
            Title: problem.Title,
            FilePath: problem.FilePath,
            LineNumber: problem.LineNumber,
            ColumnNumber: problem.ColumnNumber,
            Details: problem.Details,
            Suggestion: problem.Suggestion,
            ToolName: problem.ToolName,
            ClientName: problem.ClientName,
            RuleId: problem.RuleId,
            IsResolved: problem.IsResolved);
    }
}

/// <summary>JSON-serializable container for problem analysis results.</summary>
[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
internal sealed record DebuggerProblemsAnalysisJson(
    [property: JsonPropertyName("totalProblems")] int TotalProblems,
    [property: JsonPropertyName("errorCount")] int ErrorCount,
    [property: JsonPropertyName("warningCount")] int WarningCount,
    [property: JsonPropertyName("affectedFiles")] int AffectedFileCount,
    [property: JsonPropertyName("problemsByType")] Dictionary<string, int> ProblemsByType,
    [property: JsonPropertyName("problemsByFile")] Dictionary<string, int>? ProblemsByFile,
    [property: JsonPropertyName("problems")] List<DebuggerProblemJson> Problems,
    [property: JsonPropertyName("capturedAt")] DateTime CapturedAt,
    [property: JsonPropertyName("oldestProblem")] DateTime? OldestProblemTime,
    [property: JsonPropertyName("newestProblem")] DateTime? NewestProblemTime)
{
    /// <summary>Create a JSON analysis from stats and problems.</summary>
    public static DebuggerProblemsAnalysisJson FromStats(
        DebuggerProblemStats stats,
        IReadOnlyList<DebuggerProblem> problems,
        bool includeFileBreakdown = false)
    {
        return new DebuggerProblemsAnalysisJson(
            TotalProblems: stats.TotalProblemCount,
            ErrorCount: stats.ErrorCount,
            WarningCount: stats.WarningCount,
            AffectedFileCount: stats.AffectedFiles.Count,
            ProblemsByType: stats.ProblemCountByType,
            ProblemsByFile: includeFileBreakdown ? stats.ProblemCountByFile : null,
            Problems: problems.Select(DebuggerProblemJson.FromProblem).ToList(),
            CapturedAt: DateTime.UtcNow,
            OldestProblemTime: stats.OldestProblemTime,
            NewestProblemTime: stats.NewestProblemTime);
    }
}
