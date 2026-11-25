using Microsoft.Extensions.Logging.Abstractions;
using AiDevTools.TokenSaver.Common.Core.Contracts;
using AiDevTools.TokenSaver.MCP.Services;
using Xunit;

namespace AiDevTools.TokenSaver.MCP.Tests;

public sealed class DebuggerIntegrationTests
{
    private readonly DebuggerEventService _eventService = new(NullLogger<DebuggerEventService>.Instance);
    private readonly DebuggerProblemService _problemService = new(NullLogger<DebuggerProblemService>.Instance);
    private readonly DebuggerEventStreamManager _streamManager;

    public DebuggerIntegrationTests()
    {
        _streamManager = new DebuggerEventStreamManager(
            NullLogger<DebuggerEventStreamManager>.Instance,
            _eventService);
    }

    [Fact]
    public async Task StreamingWithFiltering_RoutesCorrectly()
    {
        var errorEvents = new List<DebuggerEvent>();
        var infoEvents = new List<DebuggerEvent>();

        // Subscribe to errors only
        var errorSub = _streamManager.AddSubscription(
            onEventAsync: async evt =>
            {
                errorEvents.Add(evt);
                await Task.CompletedTask;
            },
            filter: evt => evt.Severity == DebugSeverity.Error);

        // Subscribe to info only
        var infoSub = _streamManager.AddSubscription(
            onEventAsync: async evt =>
            {
                infoEvents.Add(evt);
                await Task.CompletedTask;
            },
            filter: evt => evt.Severity == DebugSeverity.Info);

        // Record mixed events
        _eventService.RecordToolExecution("Tool1", 100, null, null, null, false); // Info
        _eventService.RecordToolExecution("Tool2", 200, null, null, null, true);  // Error
        _eventService.RecordToolExecution("Tool3", 300, null, null, null, false); // Info
        _eventService.RecordToolExecution("Tool4", 400, null, null, null, true);  // Error

        var allEvents = _eventService.GetAllEvents();

        // Broadcast all events
        foreach (var evt in allEvents)
        {
            await _streamManager.BroadcastEventAsync(evt);
        }

        // Verify routing
        Assert.Equal(2, errorEvents.Count);
        Assert.All(errorEvents, e => Assert.Equal(DebugSeverity.Error, e.Severity));

        Assert.Equal(2, infoEvents.Count);
        Assert.All(infoEvents, e => Assert.Equal(DebugSeverity.Info, e.Severity));

        _streamManager.RemoveSubscription(errorSub);
        _streamManager.RemoveSubscription(infoSub);
    }

    [Fact]
    public void ProblemAnalysis_ByFileAndSeverity()
    {
        // Record problems from multiple files
        _problemService.RecordProblem("CompilationError", "Error 1", "Service.cs", 10, clientName: "VsCode");
        _problemService.RecordProblem("CompilationError", "Error 2", "Service.cs", 20, clientName: "VsCode");
        _problemService.RecordProblem("Warning", "Warning 1", "Controller.cs", 5, clientName: "VsCode");
        _problemService.RecordProblem("CompilationError", "Error 3", "Model.cs", 15, clientName: "Continue");

        // Group by file
        var byFile = _problemService.GetProblemsByFile();
        Assert.Equal(3, byFile.Count);
        Assert.Equal(2, byFile["Service.cs"].Count);
        Assert.Single(byFile["Controller.cs"]);

        // Group by severity
        var bySeverity = _problemService.GetProblemsBySeverity();
        Assert.Equal(3, bySeverity[DebugSeverity.Error].Count);
        Assert.Single(bySeverity[DebugSeverity.Warning]);

        // Get statistics
        var stats = _problemService.GetProblemStats();
        Assert.Equal(4, stats.TotalProblemCount);
        Assert.Equal(3, stats.ErrorCount);
        Assert.Equal(1, stats.WarningCount);
        Assert.Equal(3, stats.AffectedFiles.Count);
    }

    [Fact]
    public void EventQueryingWithMultipleFilters()
    {
        // Record diverse events
        for (int i = 0; i < 10; i++)
        {
            _eventService.RecordToolExecution(
                toolName: $"Tool{i % 3}",
                durationMs: 100 + (i * 10),
                clientName: i % 2 == 0 ? "VsCode" : "Cursor",
                isError: i % 4 == 0);
        }

        // Query by multiple filters
        var vscodeErrors = _eventService.QueryEvents(
            clientName: "VsCode",
            severity: DebugSeverity.Error);

        Assert.NotEmpty(vscodeErrors);
        Assert.All(vscodeErrors, e =>
        {
            Assert.Equal("VsCode", e.CallerInfo.ClientName);
            Assert.Equal(DebugSeverity.Error, e.Severity);
        });

        // Query by tool and severity
        var tool0Errors = _eventService.QueryEvents(
            toolName: "Tool0",
            severity: DebugSeverity.Error);

        Assert.NotEmpty(tool0Errors);
        Assert.All(tool0Errors, e => Assert.Equal("Tool0", e.ToolName));
    }
}
