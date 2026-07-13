using Microsoft.Extensions.Logging.Abstractions;
using TokenSaver.Common.Core.Contracts;
using TokenSaver.MCP.Services;
using Xunit;

namespace TokenSaver.MCP.Tests;

public sealed class DebuggerEventServiceTests
{
    private readonly DebuggerEventService _service = new(NullLogger<DebuggerEventService>.Instance);

    [Fact]
    public void RecordToolExecution_StoresEventWithCorrectProperties()
    {
        _service.RecordToolExecution("TestTool", 500, "VsCode", "1.0.0", "Claude", false);

        var events = _service.GetAllEvents();
        Assert.Single(events);

        var evt = events[0];
        Assert.Equal("TestTool", evt.ToolName);
        Assert.Equal(TimeSpan.FromMilliseconds(500), evt.Duration);
        Assert.Equal("VsCode", evt.CallerInfo.ClientName);
        Assert.Equal("1.0.0", evt.CallerInfo.ClientVersion);
        Assert.Equal("Claude", evt.CallerInfo.ModelHint);
        Assert.Equal(DebuggerEventType.ToolExecution, evt.EventType);
        Assert.Equal(DebugSeverity.Info, evt.Severity);
    }

    [Fact]
    public void RecordToolExecution_WithError_SetsSeverityToError()
    {
        _service.RecordToolExecution("FailedTool", 100, "VsCode", null, null, isError: true, "Something went wrong");

        var events = _service.GetAllEvents();
        var evt = events[0];
        Assert.Equal(DebugSeverity.Error, evt.Severity);
        Assert.True((bool)evt.Data["isError"]);
        Assert.Equal("Something went wrong", evt.Data["errorMessage"]);
    }

    [Fact]
    public void RecordException_StoresExceptionDetails()
    {
        // Throw-and-catch so the exception carries a real stack trace (an unthrown
        // exception has StackTrace == null), matching how exceptions are recorded in practice.
        Exception exception;
        try
        {
            throw new InvalidOperationException("Test error");
        }
        catch (InvalidOperationException ex)
        {
            exception = ex;
        }

        _service.RecordException(exception, "TestTool", "VsCode", "session-123");

        var events = _service.GetAllEvents();
        var evt = events[0];

        Assert.Equal(DebuggerEventType.Exception, evt.EventType);
        Assert.Equal(DebugSeverity.Error, evt.Severity);
        Assert.Equal("TestTool", evt.ToolName);
        Assert.Equal("VsCode", evt.CallerInfo.ClientName);
        Assert.Equal("session-123", evt.SessionId);
        Assert.Contains("InvalidOperationException", evt.Data["exceptionType"].ToString()!);
        Assert.Equal("Test error", evt.Data["message"]);
        Assert.NotNull(evt.StackTrace);
    }

    [Fact]
    public void RecordStateChange_StoresStateTransition()
    {
        var data = new Dictionary<string, object> { { "breakpointId", "bp-001" } };
        _service.RecordStateChange("BreakpointHit", "session-123", "VsCode", data);

        var events = _service.GetAllEvents();
        var evt = events[0];

        Assert.Equal(DebuggerEventType.StateChange, evt.EventType);
        Assert.Equal("BreakpointHit", evt.ToolName);
        Assert.Equal("session-123", evt.SessionId);
        Assert.Equal("bp-001", evt.Data["breakpointId"]);
    }

    [Fact]
    public void RecordMetric_StoresPerformanceData()
    {
        _service.RecordMetric("ContextBuildTime", 1234.5, "ms", "BuildContext", "VsCode");

        var events = _service.GetAllEvents();
        var evt = events[0];

        Assert.Equal(DebuggerEventType.Metric, evt.EventType);
        Assert.Equal("BuildContext", evt.ToolName);
        Assert.Equal("VsCode", evt.CallerInfo.ClientName);
        Assert.Equal("ContextBuildTime", evt.Data["metricName"]);
        Assert.Equal(1234.5, evt.Data["value"]);
        Assert.Equal("ms", evt.Data["unit"]);
    }

    [Fact]
    public void GetAllEvents_WithLimit_ReturnsOnlyLatestN()
    {
        for (int i = 0; i < 10; i++)
        {
            _service.RecordToolExecution($"Tool{i}", 100 * i);
        }

        var latest5 = _service.GetAllEvents(5);
        Assert.Equal(5, latest5.Count);
        Assert.Equal("Tool9", latest5.Last().ToolName);
        Assert.Equal("Tool5", latest5.First().ToolName);
    }

    [Fact]
    public void QueryEvents_ByEventType_FiltersCorrectly()
    {
        _service.RecordToolExecution("Tool1", 100);
        _service.RecordException(new Exception("Test"));
        _service.RecordToolExecution("Tool2", 200);

        var toolEvents = _service.QueryEvents(eventType: DebuggerEventType.ToolExecution);
        Assert.Equal(2, toolEvents.Count);
        Assert.All(toolEvents, e => Assert.Equal(DebuggerEventType.ToolExecution, e.EventType));

        var exceptionEvents = _service.QueryEvents(eventType: DebuggerEventType.Exception);
        Assert.Single(exceptionEvents);
    }

    [Fact]
    public void QueryEvents_BySeverity_FiltersCorrectly()
    {
        _service.RecordToolExecution("Tool1", 100, isError: false);
        _service.RecordToolExecution("Tool2", 200, isError: true);
        _service.RecordException(new Exception("Test"));

        var errors = _service.QueryEvents(severity: DebugSeverity.Error);
        Assert.Equal(2, errors.Count);
        Assert.All(errors, e => Assert.Equal(DebugSeverity.Error, e.Severity));
    }

    [Fact]
    public void QueryEvents_ByClientName_FiltersCorrectly()
    {
        _service.RecordToolExecution("Tool1", 100, "VsCode");
        _service.RecordToolExecution("Tool2", 200, "Continue");
        _service.RecordToolExecution("Tool3", 300, "VsCode");

        var vsCodeEvents = _service.QueryEvents(clientName: "VsCode");
        Assert.Equal(2, vsCodeEvents.Count);
        Assert.All(vsCodeEvents, e => Assert.Equal("VsCode", e.CallerInfo.ClientName));
    }

    [Fact]
    public void QueryEvents_ByToolName_FiltersCorrectly()
    {
        _service.RecordToolExecution("BuildContext", 100);
        _service.RecordToolExecution("SearchCode", 200);
        _service.RecordToolExecution("BuildContext", 300);

        var buildEvents = _service.QueryEvents(toolName: "BuildContext");
        Assert.Equal(2, buildEvents.Count);
        Assert.All(buildEvents, e => Assert.Equal("BuildContext", e.ToolName));
    }

    [Fact]
    public void QueryEvents_ByTimeRange_FiltersCorrectly()
    {
        // Real time gaps around middleTime: Tool1 is stamped before it, Tool2 after it.
        // (The service stamps DateTime.UtcNow at record time, so the window must reflect wall clock.)
        _service.RecordToolExecution("Tool1", 100);
        Thread.Sleep(15);
        var middleTime = DateTime.UtcNow;
        Thread.Sleep(15);
        _service.RecordToolExecution("Tool2", 200);

        var eventsInRange = _service.QueryEvents(startTime: middleTime);
        Assert.Single(eventsInRange);
        Assert.Equal("Tool2", eventsInRange[0].ToolName);
    }

    [Fact]
    public void GetEventStats_ReturnsCorrectCounts()
    {
        _service.RecordToolExecution("Tool1", 100, "VsCode", isError: false);
        _service.RecordToolExecution("Tool2", 200, "Continue", isError: true);
        _service.RecordException(new Exception("Test"), clientName: "VsCode");

        var stats = _service.GetEventStats();

        Assert.Equal(3, stats.TotalEventCount);
        Assert.Equal(2, stats.EventCountByType[DebuggerEventType.ToolExecution]);
        Assert.Equal(1, stats.EventCountByType[DebuggerEventType.Exception]);
        // Tool1 is Info; Tool2 (isError) and the exception are Error → Info=1, Error=2.
        Assert.Equal(1, stats.EventCountBySeverity[DebugSeverity.Info]);
        Assert.Equal(2, stats.EventCountBySeverity[DebugSeverity.Error]);
        Assert.Equal(2, stats.EventCountByClient["VsCode"]);
        Assert.Equal(1, stats.EventCountByClient["Continue"]);
    }

    [Fact]
    public void RecordingEvents_Respects_MaxEventsInMemory()
    {
        var smallService = new DebuggerEventService(NullLogger<DebuggerEventService>.Instance, maxEventsInMemory: 5);

        for (int i = 0; i < 10; i++)
        {
            smallService.RecordToolExecution($"Tool{i}", 100);
        }

        var events = smallService.GetAllEvents();
        Assert.Equal(5, events.Count);
        Assert.Equal("Tool5", events.First().ToolName);
        Assert.Equal("Tool9", events.Last().ToolName);
    }

    [Fact]
    public void Clear_RemovesAllEvents()
    {
        _service.RecordToolExecution("Tool1", 100);
        _service.RecordToolExecution("Tool2", 200);

        var before = _service.GetAllEvents();
        Assert.Equal(2, before.Count);

        _service.Clear();

        var after = _service.GetAllEvents();
        Assert.Empty(after);
    }

    [Fact]
    public void EventsHaveUniqueIds()
    {
        _service.RecordToolExecution("Tool1", 100);
        _service.RecordToolExecution("Tool2", 200);
        _service.RecordToolExecution("Tool3", 300);

        var events = _service.GetAllEvents();
        var ids = events.Select(e => e.Id).ToList();

        Assert.Equal(3, ids.Distinct().Count());
    }

    [Fact]
    public void EventTimestamps_AreInUtc()
    {
        var beforeRecord = DateTime.UtcNow;
        _service.RecordToolExecution("Tool1", 100);
        var afterRecord = DateTime.UtcNow;

        var events = _service.GetAllEvents();
        var evt = events[0];

        Assert.True(evt.Timestamp >= beforeRecord);
        Assert.True(evt.Timestamp <= afterRecord);
        Assert.Equal(DateTimeKind.Utc, evt.Timestamp.Kind);
    }
}
