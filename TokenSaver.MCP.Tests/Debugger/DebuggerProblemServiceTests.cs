using Microsoft.Extensions.Logging.Abstractions;
using AiDevTools.TokenSaver.Common.Core.Contracts;
using AiDevTools.TokenSaver.MCP.Services;
using Xunit;

namespace AiDevTools.TokenSaver.MCP.Tests;

public sealed class DebuggerProblemServiceTests
{
    private readonly DebuggerProblemService _service = new(NullLogger<DebuggerProblemService>.Instance);

    [Fact]
    public void RecordProblem_StoresAllProperties()
    {
        _service.RecordProblem(
            problemType: "CompilationError",
            title: "Type not found",
            filePath: "src/Service.cs",
            lineNumber: 42,
            columnNumber: 15,
            details: "Cannot resolve type 'IService'",
            suggestion: "Add using statement",
            toolName: "Roslyn",
            clientName: "VsCode",
            ruleId: "CS0246");

        var problems = _service.GetAllProblems();
        Assert.Single(problems);

        var problem = problems[0];
        Assert.Equal("CompilationError", problem.ProblemType);
        Assert.Equal("Type not found", problem.Title);
        Assert.Equal("src/Service.cs", problem.FilePath);
        Assert.Equal(42, problem.LineNumber);
        Assert.Equal(15, problem.ColumnNumber);
        Assert.Equal("Cannot resolve type 'IService'", problem.Details);
        Assert.Equal("Add using statement", problem.Suggestion);
        Assert.Equal("Roslyn", problem.ToolName);
        Assert.Equal("VsCode", problem.ClientName);
        Assert.Equal("CS0246", problem.RuleId);
    }

    [Fact]
    public void RecordProblem_ClassifiesSeverityCorrectly()
    {
        _service.RecordProblem("CompilationError", "Error 1");
        _service.RecordProblem("RuntimeError", "Error 2");
        _service.RecordProblem("Warning", "Warning 1");
        _service.RecordProblem("CodeSmell", "Smell 1");
        _service.RecordProblem("FatalCrash", "Fatal 1");

        var problems = _service.GetAllProblems();

        Assert.Equal(DebugSeverity.Error, problems[0].Severity);
        Assert.Equal(DebugSeverity.Error, problems[1].Severity);
        Assert.Equal(DebugSeverity.Warning, problems[2].Severity);
        Assert.Equal(DebugSeverity.Warning, problems[3].Severity);
        Assert.Equal(DebugSeverity.Fatal, problems[4].Severity);
    }

    [Fact]
    public void GetAllProblems_WithLimit_ReturnsOnlyLatestN()
    {
        for (int i = 0; i < 10; i++)
        {
            _service.RecordProblem("Warning", $"Warning {i}", filePath: $"file{i}.cs");
        }

        var latest3 = _service.GetAllProblems(3);
        Assert.Equal(3, latest3.Count);
        Assert.Equal("Warning 9", latest3[^1].Title);
        Assert.Equal("Warning 7", latest3[0].Title);
    }

    [Fact]
    public void QueryProblems_ByProblemType_FiltersCorrectly()
    {
        _service.RecordProblem("CompilationError", "Error 1");
        _service.RecordProblem("Warning", "Warning 1");
        _service.RecordProblem("CompilationError", "Error 2");

        var errors = _service.QueryProblems(problemType: "CompilationError");
        Assert.Equal(2, errors.Count);
        Assert.All(errors, p => Assert.Equal("CompilationError", p.ProblemType));
    }

    [Fact]
    public void QueryProblems_BySeverity_FiltersCorrectly()
    {
        _service.RecordProblem("CompilationError", "Error 1");
        _service.RecordProblem("Warning", "Warning 1");
        _service.RecordProblem("CompilationError", "Error 2");

        var errors = _service.QueryProblems(severity: DebugSeverity.Error);
        Assert.Equal(2, errors.Count);
        Assert.All(errors, p => Assert.Equal(DebugSeverity.Error, p.Severity));
    }

    [Fact]
    public void QueryProblems_ByFilePath_FiltersCorrectly()
    {
        _service.RecordProblem("Error", "Error 1", filePath: "Service.cs");
        _service.RecordProblem("Error", "Error 2", filePath: "Controller.cs");
        _service.RecordProblem("Error", "Error 3", filePath: "Service.cs");

        var serviceErrors = _service.QueryProblems(filePath: "Service.cs");
        Assert.Equal(2, serviceErrors.Count);
        Assert.All(serviceErrors, p => Assert.Equal("Service.cs", p.FilePath));
    }

    [Fact]
    public void QueryProblems_ByToolName_FiltersCorrectly()
    {
        _service.RecordProblem("Error", "Error 1", toolName: "Roslyn");
        _service.RecordProblem("Error", "Error 2", toolName: "StyleCop");
        _service.RecordProblem("Error", "Error 3", toolName: "Roslyn");

        var roslynErrors = _service.QueryProblems(toolName: "Roslyn");
        Assert.Equal(2, roslynErrors.Count);
        Assert.All(roslynErrors, p => Assert.Equal("Roslyn", p.ToolName));
    }

    [Fact]
    public void QueryProblems_ByClientName_FiltersCorrectly()
    {
        _service.RecordProblem("Error", "Error 1", clientName: "VsCode");
        _service.RecordProblem("Error", "Error 2", clientName: "Continue");
        _service.RecordProblem("Error", "Error 3", clientName: "VsCode");

        var vscodeProblems = _service.QueryProblems(clientName: "VsCode");
        Assert.Equal(2, vscodeProblems.Count);
        Assert.All(vscodeProblems, p => Assert.Equal("VsCode", p.ClientName));
    }

    [Fact]
    public void QueryProblems_ByTimeRange_FiltersCorrectly()
    {
        // Real time gaps around middleTime: "Error 1" is stamped before it, "Error 2" after it.
        // (The service stamps DateTime.UtcNow at record time, so the window must reflect wall clock.)
        _service.RecordProblem("Error", "Error 1");
        Thread.Sleep(15);
        var middleTime = DateTime.UtcNow;
        Thread.Sleep(15);
        _service.RecordProblem("Error", "Error 2");

        var problemsInRange = _service.QueryProblems(startTime: middleTime);
        Assert.Single(problemsInRange);
        Assert.Equal("Error 2", problemsInRange[0].Title);
    }

    [Fact]
    public void GetProblemsByFile_GroupsCorrectly()
    {
        _service.RecordProblem("Error", "Error 1", filePath: "Service.cs");
        _service.RecordProblem("Warning", "Warning 1", filePath: "Service.cs");
        _service.RecordProblem("Error", "Error 2", filePath: "Controller.cs");

        var byFile = _service.GetProblemsByFile();

        Assert.Equal(2, byFile.Count);
        Assert.Equal(2, byFile["Service.cs"].Count);
        Assert.Single(byFile["Controller.cs"]);
    }

    [Fact]
    public void GetProblemsByFile_WithFilter_GroupsFilteredProblems()
    {
        _service.RecordProblem("CompilationError", "Error 1", filePath: "Service.cs");
        _service.RecordProblem("Warning", "Warning 1", filePath: "Service.cs");
        _service.RecordProblem("CompilationError", "Error 2", filePath: "Controller.cs");

        var byFile = _service.GetProblemsByFile(filterProblemType: "CompilationError");

        Assert.Equal(2, byFile.Count);
        Assert.Single(byFile["Service.cs"]);
        Assert.Single(byFile["Controller.cs"]);
    }

    [Fact]
    public void GetProblemsBySeverity_GroupsCorrectly()
    {
        _service.RecordProblem("CompilationError", "Error 1");
        _service.RecordProblem("Warning", "Warning 1");
        _service.RecordProblem("CompilationError", "Error 2");
        _service.RecordProblem("Warning", "Warning 2");

        var bySeverity = _service.GetProblemsBySeverity();

        Assert.Equal(2, bySeverity[DebugSeverity.Error].Count);
        Assert.Equal(2, bySeverity[DebugSeverity.Warning].Count);
    }

    [Fact]
    public void GetProblemStats_ReturnsCorrectCounts()
    {
        _service.RecordProblem("CompilationError", "Error 1", filePath: "Service.cs");
        _service.RecordProblem("Warning", "Warning 1", filePath: "Controller.cs");
        _service.RecordProblem("CompilationError", "Error 2", filePath: "Service.cs");

        var stats = _service.GetProblemStats();

        Assert.Equal(3, stats.TotalProblemCount);
        Assert.Equal(2, stats.ErrorCount);
        Assert.Equal(1, stats.WarningCount);
        Assert.Equal(2, stats.ProblemCountByType["CompilationError"]);
        Assert.Equal(1, stats.ProblemCountByType["Warning"]);
        Assert.Equal(2, stats.ProblemCountByFile["Service.cs"]);
        Assert.Equal(1, stats.ProblemCountByFile["Controller.cs"]);
        Assert.Equal(2, stats.AffectedFiles.Count);
    }

    [Fact]
    public void RecordingProblems_Respects_MaxProblemsInMemory()
    {
        var smallService = new DebuggerProblemService(NullLogger<DebuggerProblemService>.Instance, maxProblemsInMemory: 5);

        for (int i = 0; i < 10; i++)
        {
            smallService.RecordProblem("Warning", $"Warning {i}");
        }

        var problems = smallService.GetAllProblems();
        Assert.Equal(5, problems.Count);
        Assert.Equal("Warning 5", problems[0].Title);
        Assert.Equal("Warning 9", problems[^1].Title);
    }

    [Fact]
    public void Clear_RemovesAllProblems()
    {
        _service.RecordProblem("Error", "Error 1");
        _service.RecordProblem("Error", "Error 2");

        var before = _service.GetAllProblems();
        Assert.Equal(2, before.Count);

        _service.Clear();

        var after = _service.GetAllProblems();
        Assert.Empty(after);
    }

    [Fact]
    public void ProblemsHaveUniqueIds()
    {
        _service.RecordProblem("Error", "Error 1");
        _service.RecordProblem("Error", "Error 2");
        _service.RecordProblem("Error", "Error 3");

        var problems = _service.GetAllProblems();
        var ids = problems.Select(p => p.Id).ToList();

        Assert.Equal(3, ids.Distinct().Count());
    }

    [Fact]
    public void ProblemTimestamps_AreInUtc()
    {
        var beforeRecord = DateTime.UtcNow;
        _service.RecordProblem("Error", "Error 1");
        var afterRecord = DateTime.UtcNow;

        var problems = _service.GetAllProblems();
        var problem = problems[0];

        Assert.True(problem.Timestamp >= beforeRecord);
        Assert.True(problem.Timestamp <= afterRecord);
        Assert.Equal(DateTimeKind.Utc, problem.Timestamp.Kind);
    }

    [Fact]
    public void RecordProblem_WithMinimalData_StoresCorrectly()
    {
        _service.RecordProblem("Warning", "A warning");

        var problems = _service.GetAllProblems();
        var problem = problems[0];

        Assert.Equal("Warning", problem.ProblemType);
        Assert.Equal("A warning", problem.Title);
        Assert.Null(problem.FilePath);
        Assert.Null(problem.LineNumber);
        Assert.Null(problem.ColumnNumber);
        Assert.Null(problem.Details);
        Assert.Null(problem.Suggestion);
        Assert.Null(problem.ToolName);
        Assert.Null(problem.ClientName);
        Assert.Null(problem.RuleId);
        Assert.False(problem.IsResolved);
    }

    [Fact]
    public void GetEventStats_WithNoProblems_ReturnsZeroCounts()
    {
        var stats = _service.GetProblemStats();

        Assert.Equal(0, stats.TotalProblemCount);
        Assert.Equal(0, stats.ErrorCount);
        Assert.Equal(0, stats.WarningCount);
        Assert.Empty(stats.ProblemCountByType);
        Assert.Empty(stats.ProblemCountByFile);
        Assert.Empty(stats.AffectedFiles);
        Assert.Null(stats.OldestProblemTime);
        Assert.Null(stats.NewestProblemTime);
    }
}
