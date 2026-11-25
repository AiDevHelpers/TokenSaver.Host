using AiDevTools.TokenSaver.Common.Core.Contracts;
using AiDevTools.TokenSaver.MCP.Tools;

using System.Text.Json;

using Xunit;

namespace AiDevTools.TokenSaver.MCP.Tests.Tools;

/// <summary>
///     Covers DebuggerAnalysisTools with fake problem and event capture stores. Nothing is captured
///     from a live process.
/// </summary>
public sealed class DebuggerAnalysisToolsTests
{
    private static readonly DateTime Stamp = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private static DebuggerProblem Problem(string file, DebugSeverity severity, string title) =>
        new(Guid.NewGuid(), Stamp, "CompilationError", severity, title, file, 10, 1, null, null, "build", "vscode", null);

    [Fact]
    public void GetCapturedProblems_SeverityFilter_PassesParsedSeverityToStore()
    {
        FakeProblemCapture problems = new()
        {
            Queried = [Problem("a.cs", DebugSeverity.Error, "Null ref in parser")],
        };
        DebuggerAnalysisTools tools = new(new FakeEventCapture(), problems);

        string json = tools.GetCapturedProblems(severity: "error", filePath: "a.cs");

        Assert.Equal(DebugSeverity.Error, problems.LastQuerySeverity);
        Assert.Equal("a.cs", problems.LastQueryFilePath);
        Assert.Contains("Null ref in parser", json, StringComparison.Ordinal);
    }

    [Fact]
    public void GetProblemsByFile_MixedSeverities_CountsCriticalAndWarnings()
    {
        FakeProblemCapture problems = new()
        {
            ByFile = new Dictionary<string, IReadOnlyList<DebuggerProblem>>
            {
                ["a.cs"] =
                [
                    Problem("a.cs", DebugSeverity.Error, "e1"),
                    Problem("a.cs", DebugSeverity.Warning, "w1"),
                ],
                ["b.cs"] = [Problem("b.cs", DebugSeverity.Warning, "w2")],
            },
        };
        DebuggerAnalysisTools tools = new(new FakeEventCapture(), problems);

        using JsonDocument doc = JsonDocument.Parse(tools.GetProblemsByFile());
        JsonElement first = doc.RootElement.GetProperty("files")[0];

        Assert.Equal("a.cs", first.GetProperty("filePath").GetString());
        Assert.Equal(2, first.GetProperty("problemCount").GetInt32());
        Assert.Equal(1, first.GetProperty("criticalProblems").GetInt32());
        Assert.Equal(1, first.GetProperty("warnings").GetInt32());
    }

    [Fact]
    public void GetProblemsBySeverity_ReturnsGroupsKeyedBySeverityName()
    {
        FakeProblemCapture problems = new()
        {
            BySeverity = new Dictionary<DebugSeverity, IReadOnlyList<DebuggerProblem>>
            {
                [DebugSeverity.Fatal] = [Problem("a.cs", DebugSeverity.Fatal, "boom")],
            },
        };
        DebuggerAnalysisTools tools = new(new FakeEventCapture(), problems);

        using JsonDocument doc = JsonDocument.Parse(tools.GetProblemsBySeverity());
        JsonElement fatal = doc.RootElement.GetProperty("bySeverity").GetProperty("Fatal");

        Assert.Equal(1, fatal.GetProperty("count").GetInt32());
    }

    [Fact]
    public void GetProblemSummary_ReportsTotalsAndAffectedFiles()
    {
        FakeProblemCapture problems = new()
        {
            Stats = new DebuggerProblemStats(
                TotalProblemCount: 4,
                ErrorCount: 2,
                WarningCount: 2,
                ProblemCountByType: new Dictionary<string, int> { ["CompilationError"] = 4 },
                ProblemCountByFile: new Dictionary<string, int> { ["a.cs"] = 4 },
                AffectedFiles: ["a.cs"],
                OldestProblemTime: Stamp,
                NewestProblemTime: Stamp),
        };
        DebuggerAnalysisTools tools = new(new FakeEventCapture(), problems);

        using JsonDocument doc = JsonDocument.Parse(tools.GetProblemSummary());
        JsonElement summary = doc.RootElement.GetProperty("summary");

        Assert.Equal(4, summary.GetProperty("totalProblems").GetInt32());
        Assert.Equal(2, summary.GetProperty("errors").GetInt32());
        Assert.Equal(1, summary.GetProperty("affectedFiles").GetInt32());
    }

    [Fact]
    public void GetDebugEvents_TypeFilter_PassesParsedTypeAndReportsStats()
    {
        FakeEventCapture events = new()
        {
            Stats = new DebuggerEventStats(
                TotalEventCount: 7,
                EventCountByType: new Dictionary<DebuggerEventType, int> { [DebuggerEventType.Exception] = 2 },
                EventCountBySeverity: new Dictionary<DebugSeverity, int>(),
                EventCountByClient: new Dictionary<string, int>(),
                OldestEventTime: null,
                NewestEventTime: null),
        };
        DebuggerAnalysisTools tools = new(events, new FakeProblemCapture());

        using JsonDocument doc = JsonDocument.Parse(tools.GetDebugEvents(eventType: "exception", toolName: "RunTests"));

        Assert.Equal(DebuggerEventType.Exception, events.LastQueryEventType);
        Assert.Equal("RunTests", events.LastQueryToolName);
        Assert.Equal(7, doc.RootElement.GetProperty("stats").GetProperty("totalEvents").GetInt32());
    }

    [Fact]
    public void GetHighPriorityIssues_FatalAndError_CountsBoth()
    {
        FakeProblemCapture problems = new()
        {
            Queried =
            [
                Problem("a.cs", DebugSeverity.Fatal, "fatal one"),
                Problem("a.cs", DebugSeverity.Error, "error one"),
            ],
        };
        DebuggerAnalysisTools tools = new(new FakeEventCapture(), problems);

        using JsonDocument doc = JsonDocument.Parse(tools.GetHighPriorityIssues());
        JsonElement critical = doc.RootElement.GetProperty("critical");

        Assert.Equal(1, critical.GetProperty("fatalCount").GetInt32());
        Assert.Equal(1, critical.GetProperty("errorCount").GetInt32());
        Assert.Equal(2, critical.GetProperty("totalCritical").GetInt32());
    }

    [Fact]
    public void ClearAllDiagnostics_ClearsProblemsAndEvents()
    {
        FakeProblemCapture problems = new();
        FakeEventCapture events = new();
        DebuggerAnalysisTools tools = new(events, problems);

        string json = tools.ClearAllDiagnostics();

        Assert.True(problems.Cleared);
        Assert.True(events.Cleared);
        Assert.Contains("All captured problems and events have been cleared", json, StringComparison.Ordinal);
    }

    private sealed class FakeProblemCapture : IDebuggerProblemCapture
    {
        public IReadOnlyList<DebuggerProblem> Queried { get; set; } = [];
        public IReadOnlyDictionary<string, IReadOnlyList<DebuggerProblem>> ByFile { get; set; } =
            new Dictionary<string, IReadOnlyList<DebuggerProblem>>();
        public IReadOnlyDictionary<DebugSeverity, IReadOnlyList<DebuggerProblem>> BySeverity { get; set; } =
            new Dictionary<DebugSeverity, IReadOnlyList<DebuggerProblem>>();
        public DebuggerProblemStats Stats { get; set; } = new(0, 0, 0, new(), new(), [], null, null);
        public DebugSeverity? LastQuerySeverity { get; private set; }
        public string? LastQueryFilePath { get; private set; }
        public bool Cleared { get; private set; }

        public void RecordProblem(string problemType, string title, string? filePath = null, int? lineNumber = null, int? columnNumber = null,
            string? details = null, string? suggestion = null, string? toolName = null, string? clientName = null, string? ruleId = null)
        {
        }

        public IReadOnlyList<DebuggerProblem> GetAllProblems(int? limitCount = null) => Queried;

        public IReadOnlyList<DebuggerProblem> QueryProblems(string? problemType = null, DebugSeverity? severity = null, string? filePath = null,
            string? toolName = null, string? clientName = null, DateTime? startTime = null, DateTime? endTime = null, int? limitCount = null)
        {
            LastQuerySeverity = severity;
            LastQueryFilePath = filePath;
            return Queried;
        }

        public IReadOnlyDictionary<string, IReadOnlyList<DebuggerProblem>> GetProblemsByFile(string? filterProblemType = null) => ByFile;

        public IReadOnlyDictionary<DebugSeverity, IReadOnlyList<DebuggerProblem>> GetProblemsBySeverity(string? filterProblemType = null) => BySeverity;

        public DebuggerProblemStats GetProblemStats() => Stats;

        public void Clear() => Cleared = true;
    }

    private sealed class FakeEventCapture : IDebuggerEventCapture
    {
        public DebuggerEventStats Stats { get; set; } = new(0, new(), new(), new(), null, null);
        public DebuggerEventType? LastQueryEventType { get; private set; }
        public string? LastQueryToolName { get; private set; }
        public bool Cleared { get; private set; }

        public void RecordToolExecution(string toolName, long durationMs, string? clientName = null, string? clientVersion = null,
            string? modelHint = null, bool isError = false, string? errorMessage = null)
        {
        }

        public void RecordException(Exception exception, string? toolName = null, string? clientName = null, string? sessionId = null)
        {
        }

        public void RecordStateChange(string stateChange, string? sessionId = null, string? clientName = null, Dictionary<string, object>? data = null)
        {
        }

        public void RecordMetric(string metricName, double value, string? unit = null, string? toolName = null, string? clientName = null)
        {
        }

        public IReadOnlyList<DebuggerEvent> GetAllEvents(int? limitCount = null) => [];

        public IReadOnlyList<DebuggerEvent> QueryEvents(DebuggerEventType? eventType = null, DebugSeverity? severity = null, string? clientName = null,
            string? toolName = null, DateTime? startTime = null, DateTime? endTime = null, int? limitCount = null)
        {
            LastQueryEventType = eventType;
            LastQueryToolName = toolName;
            return [];
        }

        public DebuggerEventStats GetEventStats() => Stats;

        public void Clear() => Cleared = true;
    }
}
