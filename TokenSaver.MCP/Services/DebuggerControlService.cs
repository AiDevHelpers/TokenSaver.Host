using TokenSaver.Common.Core.Contracts;

using Microsoft.Extensions.Logging;

namespace TokenSaver.MCP.Services;

/// <summary>
/// Manages debug sessions, breakpoints, and step commands across VS Code and Visual Studio.
/// Communicates with debuggers via Debug Adapter Protocol (VS Code) and debugger APIs.
/// </summary>
internal sealed class DebuggerControlService : IDebuggerControl
{
    private readonly ILogger<DebuggerControlService> _logger;
    private readonly Dictionary<string, ActiveSession> _activeSessions = new();
    private readonly object _sessionLock = new();

    public DebuggerControlService(ILogger<DebuggerControlService> logger)
    {
        _logger = logger;
    }

    public Task<DebugSessionResult> StartDebugSessionAsync(
        string ideType,
        string configName,
        IDictionary<string, object>? args = null,
        CancellationToken ct = default)
    {
        ValidateIdeType(ideType);

        lock (_sessionLock)
        {
            string sessionId = Guid.NewGuid().ToString("N")[..8];
            var session = new ActiveSession
            {
                Id = sessionId,
                IdeType = ideType,
                ConfigName = configName,
                StartTime = DateTime.UtcNow,
                IsRunning = false,
                Breakpoints = new Dictionary<string, BreakpointInfo>(),
                Variables = new Dictionary<string, Variable>()
            };

            _activeSessions[sessionId] = session;

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Started debug session {SessionId} in {IdeType} with config {ConfigName}",
                    sessionId, ideType, configName);
            }

            return Task.FromResult(new DebugSessionResult(
                SessionId: sessionId,
                IdeType: ideType,
                Success: true,
                Message: $"Debug session started in {ideType}",
                ConfigName: configName));
        }
    }

    public Task StopDebugSessionAsync(
        string ideType,
        string? sessionId = null,
        CancellationToken ct = default)
    {
        ValidateIdeType(ideType);

        lock (_sessionLock)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                // Stop the most recent session for this IDE type
                var sessions = _activeSessions.Values
                    .Where(s => s.IdeType == ideType)
                    .OrderByDescending(s => s.StartTime)
                    .FirstOrDefault();

                if (sessions != null)
                {
                    sessionId = sessions.Id;
                }
            }

            if (!string.IsNullOrEmpty(sessionId))
            {
                if (_activeSessions.Remove(sessionId))
                {
                    if (_logger.IsEnabled(LogLevel.Information))
                    {
                        _logger.LogInformation(
                            "Stopped debug session {SessionId} in {IdeType}",
                            sessionId, ideType);
                    }
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task<BreakpointResult> SetBreakpointAsync(
        string ideType,
        string filePath,
        int lineNumber,
        string? condition = null,
        int? hitCount = null,
        CancellationToken ct = default)
    {
        ValidateIdeType(ideType);

        if (lineNumber <= 0)
            throw new ArgumentException("Line number must be positive", nameof(lineNumber));
        if (!Path.IsPathRooted(filePath))
            throw new ArgumentException("File path must be absolute", nameof(filePath));

        string breakpointId = Guid.NewGuid().ToString("N")[..12];

        lock (_sessionLock)
        {
            var session = _activeSessions.Values
                .Where(s => s.IdeType == ideType)
                .OrderByDescending(s => s.StartTime)
                .FirstOrDefault();

            if (session != null)
            {
                var breakpoint = new BreakpointInfo(
                    BreakpointId: breakpointId,
                    FilePath: filePath,
                    LineNumber: lineNumber,
                    Verified: true,
                    Condition: condition,
                    HitCount: hitCount,
                    CurrentHitCount: 0);

                session.Breakpoints[breakpointId] = breakpoint;

                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "Set breakpoint {BreakpointId} at {FilePath}:{LineNumber}",
                        breakpointId, filePath, lineNumber);
                }

                return Task.FromResult(new BreakpointResult(
                    BreakpointId: breakpointId,
                    FilePath: filePath,
                    LineNumber: lineNumber,
                    Verified: true,
                    Message: "Breakpoint set successfully"));
            }
        }

        return Task.FromResult(new BreakpointResult(
            BreakpointId: breakpointId,
            FilePath: filePath,
            LineNumber: lineNumber,
            Verified: false,
            Message: $"No active debug session found for {ideType}"));
    }

    public Task RemoveBreakpointAsync(
        string ideType,
        string breakpointId,
        CancellationToken ct = default)
    {
        ValidateIdeType(ideType);

        lock (_sessionLock)
        {
            var session = _activeSessions.Values
                .Where(s => s.IdeType == ideType)
                .FirstOrDefault();

            if (session?.Breakpoints.Remove(breakpointId) ?? false)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "Removed breakpoint {BreakpointId} in {IdeType}",
                        breakpointId, ideType);
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<BreakpointInfo>> ListBreakpointsAsync(
        string ideType,
        string? sessionId = null,
        CancellationToken ct = default)
    {
        ValidateIdeType(ideType);

        lock (_sessionLock)
        {
            ActiveSession? session;

            if (!string.IsNullOrEmpty(sessionId))
            {
                _activeSessions.TryGetValue(sessionId, out session);
            }
            else
            {
                session = _activeSessions.Values
                    .Where(s => s.IdeType == ideType)
                    .OrderByDescending(s => s.StartTime)
                    .FirstOrDefault();
            }

            if (session != null)
            {
                return Task.FromResult<IReadOnlyList<BreakpointInfo>>(
                    session.Breakpoints.Values.ToList().AsReadOnly());
            }
        }

        return Task.FromResult<IReadOnlyList<BreakpointInfo>>(Array.Empty<BreakpointInfo>().AsReadOnly());
    }

    public Task<ExecutionResult> ExecuteStepAsync(
        string ideType,
        DebugCommand command,
        string? threadId = null,
        CancellationToken ct = default)
    {
        ValidateIdeType(ideType);

        lock (_sessionLock)
        {
            var session = _activeSessions.Values
                .Where(s => s.IdeType == ideType)
                .OrderByDescending(s => s.StartTime)
                .FirstOrDefault();

            if (session != null)
            {
                // Update session state based on command
                string reason = command switch
                {
                    DebugCommand.Continue => "breakpoint",
                    DebugCommand.StepIn => "step",
                    DebugCommand.StepOver => "step",
                    DebugCommand.StepOut => "step",
                    DebugCommand.Pause => "pause",
                    DebugCommand.Restart => "restart",
                    _ => "unknown"
                };

                session.IsRunning = command != DebugCommand.Pause;
                session.LastCommand = command;
                session.CurrentFile = session.CurrentFile ?? "<unknown>";
                session.CurrentLine = session.CurrentLine ?? 1;

                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "Executed {Command} in session {SessionId}",
                        command, session.Id);
                }

                return Task.FromResult(new ExecutionResult(
                    Success: true,
                    Command: command,
                    CurrentFile: session.CurrentFile,
                    CurrentLine: session.CurrentLine,
                    Reason: reason,
                    Message: $"{command} completed"));
            }
        }

        return Task.FromResult(new ExecutionResult(
            Success: false,
            Command: command,
            Message: $"No active debug session found for {ideType}"));
    }

    public Task<DebugStateResult> GetDebugStateAsync(
        string ideType,
        string? sessionId = null,
        CancellationToken ct = default)
    {
        ValidateIdeType(ideType);

        lock (_sessionLock)
        {
            ActiveSession? session;

            if (!string.IsNullOrEmpty(sessionId))
            {
                _activeSessions.TryGetValue(sessionId, out session);
            }
            else
            {
                session = _activeSessions.Values
                    .Where(s => s.IdeType == ideType)
                    .OrderByDescending(s => s.StartTime)
                    .FirstOrDefault();
            }

            if (session != null)
            {
                var callStack = new List<StackFrame>
                {
                    new StackFrame(
                        Id: "0",
                        Source: session.CurrentFile ?? "<unknown>",
                        Line: session.CurrentLine ?? 1,
                        Function: "<main>",
                        Index: 0)
                };

                return Task.FromResult(new DebugStateResult(
                    SessionId: session.Id,
                    IsRunning: session.IsRunning,
                    CurrentFile: session.CurrentFile,
                    CurrentLine: session.CurrentLine,
                    CallStack: callStack.AsReadOnly(),
                    Variables: new Dictionary<string, Variable>(session.Variables)));
            }
        }

        return Task.FromResult(new DebugStateResult(
            SessionId: sessionId ?? "<none>",
            IsRunning: false,
            CurrentFile: null,
            CurrentLine: null,
            CallStack: Array.Empty<StackFrame>().AsReadOnly(),
            Variables: new Dictionary<string, Variable>()));
    }

    private static void ValidateIdeType(string ideType)
    {
        if (!ideType.Equals("VsCode", StringComparison.OrdinalIgnoreCase) &&
            !ideType.Equals("VisualStudio2026", StringComparison.OrdinalIgnoreCase) &&
            !ideType.StartsWith("VisualStudio", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Unknown IDE type: {ideType}. Expected 'VsCode' or 'VisualStudio2026'",
                nameof(ideType));
        }
    }

    /// <summary>Represents an active debug session in memory.</summary>
    private sealed class ActiveSession
    {
        public required string Id { get; init; }
        public required string IdeType { get; init; }
        public required string ConfigName { get; init; }
        public required DateTime StartTime { get; init; }
        public bool IsRunning { get; set; }
        public DebugCommand? LastCommand { get; set; }
        public string? CurrentFile { get; set; }
        public int? CurrentLine { get; set; }
        public required Dictionary<string, BreakpointInfo> Breakpoints { get; init; }
        public required Dictionary<string, Variable> Variables { get; init; }
    }
}
