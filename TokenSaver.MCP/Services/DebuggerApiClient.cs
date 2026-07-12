using TokenSaver.Common.Core.Contracts;

namespace TokenSaver.MCP.Services;

/// <summary>
/// Public API for VS Code debugger extension to communicate with the MCP debugger service.
/// HTTP endpoints at /api/debugger/* allow the VS Code extension to control debug sessions.
/// </summary>
public interface IDebuggerApiClient
{
    /// <summary>Start a debug session via HTTP API.</summary>
    Task<DebuggerApiResponse<DebugSessionResult>> StartSessionAsync(
        DebuggerStartSessionRequest request,
        CancellationToken ct = default);

    /// <summary>Stop a debug session via HTTP API.</summary>
    Task<DebuggerApiResponse> StopSessionAsync(
        string ideType,
        string? sessionId = null,
        CancellationToken ct = default);

    /// <summary>Set a breakpoint via HTTP API.</summary>
    Task<DebuggerApiResponse<BreakpointResult>> SetBreakpointAsync(
        DebuggerSetBreakpointRequest request,
        CancellationToken ct = default);

    /// <summary>Remove a breakpoint via HTTP API.</summary>
    Task<DebuggerApiResponse> RemoveBreakpointAsync(
        string ideType,
        string breakpointId,
        CancellationToken ct = default);

    /// <summary>List breakpoints via HTTP API.</summary>
    Task<DebuggerApiResponse<IReadOnlyList<BreakpointInfo>>> ListBreakpointsAsync(
        string ideType,
        string? sessionId = null,
        CancellationToken ct = default);

    /// <summary>Execute a step command via HTTP API.</summary>
    Task<DebuggerApiResponse<ExecutionResult>> ExecuteStepAsync(
        DebuggerExecuteStepRequest request,
        CancellationToken ct = default);

    /// <summary>Get debug state via HTTP API.</summary>
    Task<DebuggerApiResponse<DebugStateResult>> GetDebugStateAsync(
        string ideType,
        string? sessionId = null,
        CancellationToken ct = default);

    /// <summary>List active debug sessions via HTTP API.</summary>
    Task<DebuggerApiResponse<IReadOnlyList<DebugSessionInfo>>> ListSessionsAsync(
        string? ideType = null,
        CancellationToken ct = default);
}

/// <summary>Generic response wrapper for debugger API calls.</summary>
public sealed record DebuggerApiResponse(
    bool Success,
    string Message,
    string? Error = null,
    int? StatusCode = null)
{
    /// <summary>Create a success response.</summary>
    public static DebuggerApiResponse Ok(string message = "Success") =>
        new(Success: true, Message: message);

    /// <summary>Create an error response.</summary>
    public static DebuggerApiResponse Fail(string error, string message = "Operation failed") =>
        new(Success: false, Message: message, Error: error, StatusCode: 400);
}

/// <summary>Generic response wrapper with data payload.</summary>
public sealed record DebuggerApiResponse<T>(
    bool Success,
    string Message,
    T? Data = default,
    string? Error = null,
    int? StatusCode = null)
{
    /// <summary>Create a success response with data.</summary>
    public static DebuggerApiResponse<T> Ok(T data, string message = "Success") =>
        new(Success: true, Message: message, Data: data);

    /// <summary>Create an error response.</summary>
    public static DebuggerApiResponse<T> Fail(string error, string message = "Operation failed") =>
        new(Success: false, Message: message, Error: error, StatusCode: 400);
}

/// <summary>Request to start a debug session.</summary>
public sealed record DebuggerStartSessionRequest(
    string IdeType,
    string ConfigName,
    Dictionary<string, object>? Args = null);

/// <summary>Request to set a breakpoint.</summary>
public sealed record DebuggerSetBreakpointRequest(
    string IdeType,
    string FilePath,
    int LineNumber,
    string? Condition = null,
    int? HitCount = null);

/// <summary>Request to execute a step command.</summary>
public sealed record DebuggerExecuteStepRequest(
    string IdeType,
    DebugCommand Command,
    string? ThreadId = null);

/// <summary>Summary info about an active debug session.</summary>
public sealed record DebugSessionInfo(
    string SessionId,
    string IdeType,
    string ConfigName,
    DateTime StartTime,
    bool IsRunning,
    int BreakpointCount,
    string? CurrentFile = null,
    int? CurrentLine = null);
