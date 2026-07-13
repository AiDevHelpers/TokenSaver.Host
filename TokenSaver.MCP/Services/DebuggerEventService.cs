using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using TokenSaver.Common.Core.Contracts;

namespace TokenSaver.MCP.Services;

/// <summary>
/// Captures structured debug events (tool execution, exceptions, state changes, metrics)
/// for monitoring across extensions.
/// Maintains an in-memory rolling buffer and supports filtering/querying by multiple dimensions.
/// </summary>
internal sealed class DebuggerEventService : IDebuggerEventCapture
{
    private readonly ILogger<DebuggerEventService> _logger;
    private readonly int _maxEventsInMemory;
    private readonly ConcurrentQueue<DebuggerEvent> _eventBuffer;
    private readonly object _capacityLock = new();

    public DebuggerEventService(ILogger<DebuggerEventService> logger, int maxEventsInMemory = 10000)
    {
        _logger = logger;
        _maxEventsInMemory = maxEventsInMemory;
        _eventBuffer = new ConcurrentQueue<DebuggerEvent>();
    }

    public void RecordToolExecution(
        string toolName,
        long durationMs,
        string? clientName = null,
        string? clientVersion = null,
        string? modelHint = null,
        bool isError = false,
        string? errorMessage = null)
    {
        var evt = new DebuggerEvent(
            Id: Guid.NewGuid(),
            Timestamp: DateTime.UtcNow,
            EventType: DebuggerEventType.ToolExecution,
            Severity: isError ? DebugSeverity.Error : DebugSeverity.Info,
            CallerInfo: new DebuggerCallerInfo(
                ClientName: clientName,
                ClientVersion: clientVersion,
                ModelHint: modelHint,
                IdeType: null),
            ToolName: toolName,
            Duration: TimeSpan.FromMilliseconds(durationMs),
            Data: new Dictionary<string, object>
            {
                { "isError", isError },
                { "errorMessage", errorMessage ?? "" }
            },
            StackTrace: null,
            SessionId: null);

        EnqueueEvent(evt);
    }

    public void RecordException(
        Exception exception,
        string? toolName = null,
        string? clientName = null,
        string? sessionId = null)
    {
        var evt = new DebuggerEvent(
            Id: Guid.NewGuid(),
            Timestamp: DateTime.UtcNow,
            EventType: DebuggerEventType.Exception,
            Severity: DebugSeverity.Error,
            CallerInfo: new DebuggerCallerInfo(
                ClientName: clientName,
                ClientVersion: null,
                ModelHint: null,
                IdeType: null),
            ToolName: toolName,
            Duration: null,
            Data: new Dictionary<string, object>
            {
                { "exceptionType", exception.GetType().FullName ?? "Unknown" },
                { "message", exception.Message }
            },
            StackTrace: exception.StackTrace,
            SessionId: sessionId);

        EnqueueEvent(evt);
        _logger.LogError(exception, "Exception recorded in debugger: {ToolName}", toolName);
    }

    public void RecordStateChange(
        string stateChange,
        string? sessionId = null,
        string? clientName = null,
        Dictionary<string, object>? data = null)
    {
        var evt = new DebuggerEvent(
            Id: Guid.NewGuid(),
            Timestamp: DateTime.UtcNow,
            EventType: DebuggerEventType.StateChange,
            Severity: DebugSeverity.Info,
            CallerInfo: new DebuggerCallerInfo(
                ClientName: clientName,
                ClientVersion: null,
                ModelHint: null,
                IdeType: null),
            ToolName: stateChange,
            Duration: null,
            Data: data ?? new(),
            StackTrace: null,
            SessionId: sessionId);

        EnqueueEvent(evt);
    }

    public void RecordMetric(
        string metricName,
        double value,
        string? unit = null,
        string? toolName = null,
        string? clientName = null)
    {
        var evt = new DebuggerEvent(
            Id: Guid.NewGuid(),
            Timestamp: DateTime.UtcNow,
            EventType: DebuggerEventType.Metric,
            Severity: DebugSeverity.Info,
            CallerInfo: new DebuggerCallerInfo(
                ClientName: clientName,
                ClientVersion: null,
                ModelHint: null,
                IdeType: null),
            ToolName: toolName ?? metricName,
            Duration: null,
            Data: new Dictionary<string, object>
            {
                { "metricName", metricName },
                { "value", value },
                { "unit", unit ?? "" }
            },
            StackTrace: null,
            SessionId: null);

        EnqueueEvent(evt);
    }

    public IReadOnlyList<DebuggerEvent> GetAllEvents(int? limitCount = null)
    {
        var all = _eventBuffer.ToList();
        if (limitCount.HasValue && limitCount > 0)
        {
            all = all.TakeLast(limitCount.Value).ToList();
        }
        return all.AsReadOnly();
    }

    public IReadOnlyList<DebuggerEvent> QueryEvents(
        DebuggerEventType? eventType = null,
        DebugSeverity? severity = null,
        string? clientName = null,
        string? toolName = null,
        DateTime? startTime = null,
        DateTime? endTime = null,
        int? limitCount = null)
    {
        var filtered = _eventBuffer.Where(e =>
            (eventType == null || e.EventType == eventType)
            && (severity == null || e.Severity == severity)
            && (string.IsNullOrEmpty(clientName) || e.CallerInfo.ClientName == clientName)
            && (string.IsNullOrEmpty(toolName) || e.ToolName == toolName)
            && (startTime == null || e.Timestamp >= startTime)
            && (endTime == null || e.Timestamp <= endTime))
            .ToList();

        if (limitCount.HasValue && limitCount > 0)
        {
            filtered = filtered.TakeLast(limitCount.Value).ToList();
        }

        return filtered.AsReadOnly();
    }

    public DebuggerEventStats GetEventStats()
    {
        var allEvents = _eventBuffer.ToList();
        return new DebuggerEventStats(
            TotalEventCount: allEvents.Count,
            EventCountByType: allEvents.GroupBy(e => e.EventType)
                .ToDictionary(g => g.Key, g => g.Count()),
            EventCountBySeverity: allEvents.GroupBy(e => e.Severity)
                .ToDictionary(g => g.Key, g => g.Count()),
            EventCountByClient: allEvents.GroupBy(e => e.CallerInfo.ClientName)
                .Where(g => !string.IsNullOrEmpty(g.Key))
                .ToDictionary(g => g.Key!, g => g.Count()),
            OldestEventTime: allEvents.MinBy(e => e.Timestamp)?.Timestamp,
            NewestEventTime: allEvents.MaxBy(e => e.Timestamp)?.Timestamp);
    }

    public void Clear()
    {
        while (_eventBuffer.TryDequeue(out _))
        {
        }
    }

    private void EnqueueEvent(DebuggerEvent evt)
    {
        _eventBuffer.Enqueue(evt);

        lock (_capacityLock)
        {
            while (_eventBuffer.Count > _maxEventsInMemory && _eventBuffer.TryDequeue(out _))
            {
            }
        }
    }
}
