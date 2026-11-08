using TokenSaver.Common.Core.Contracts;

using Microsoft.Extensions.Logging;

namespace TokenSaver.MCP.Services;

/// <summary>
/// HTTP API implementation that bridges the debugger HTTP endpoints with IDebuggerControl service.
/// Provides the public interface for VS Code debugger extension communication.
/// </summary>
internal sealed class DebuggerApiImplementation : IDebuggerApiClient
{
    private readonly IDebuggerControl _debugger;
    private readonly ILogger<DebuggerApiImplementation> _logger;

    public DebuggerApiImplementation(
        IDebuggerControl debugger,
        ILogger<DebuggerApiImplementation> logger)
    {
        _debugger = debugger;
        _logger = logger;
    }

    public async Task<DebuggerApiResponse<DebugSessionResult>> StartSessionAsync(
        DebuggerStartSessionRequest request,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _debugger.StartDebugSessionAsync(
                request.IdeType,
                request.ConfigName,
                request.Args,
                ct);

            return result.Success
                ? DebuggerApiResponse.Ok<DebugSessionResult>(result, "Debug session started")
                : DebuggerApiResponse.Fail<DebugSessionResult>(result.Message);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("Invalid start session request: {Error}", ex.Message);
            return DebuggerApiResponse.Fail<DebugSessionResult>(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting debug session");
            return DebuggerApiResponse.Fail<DebugSessionResult>(ex.Message);
        }
    }

    public async Task<DebuggerApiResponse> StopSessionAsync(
        string ideType,
        string? sessionId = null,
        CancellationToken ct = default)
    {
        try
        {
            await _debugger.StopDebugSessionAsync(ideType, sessionId, ct);
            return DebuggerApiResponse.Ok("Debug session stopped");
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("Invalid stop session request: {Error}", ex.Message);
            return DebuggerApiResponse.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping debug session");
            return DebuggerApiResponse.Fail(ex.Message);
        }
    }

    public async Task<DebuggerApiResponse<BreakpointResult>> SetBreakpointAsync(
        DebuggerSetBreakpointRequest request,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _debugger.SetBreakpointAsync(
                request.IdeType,
                request.FilePath,
                request.LineNumber,
                request.Condition,
                request.HitCount,
                ct);

            return result.Verified
                ? DebuggerApiResponse.Ok<BreakpointResult>(result, "Breakpoint set")
                : DebuggerApiResponse.Fail<BreakpointResult>(result.Message);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("Invalid breakpoint request: {Error}", ex.Message);
            return DebuggerApiResponse.Fail<BreakpointResult>(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting breakpoint");
            return DebuggerApiResponse.Fail<BreakpointResult>(ex.Message);
        }
    }

    public async Task<DebuggerApiResponse> RemoveBreakpointAsync(
        string ideType,
        string breakpointId,
        CancellationToken ct = default)
    {
        try
        {
            await _debugger.RemoveBreakpointAsync(ideType, breakpointId, ct);
            return DebuggerApiResponse.Ok("Breakpoint removed");
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("Invalid remove breakpoint request: {Error}", ex.Message);
            return DebuggerApiResponse.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing breakpoint");
            return DebuggerApiResponse.Fail(ex.Message);
        }
    }

    public async Task<DebuggerApiResponse<IReadOnlyList<BreakpointInfo>>> ListBreakpointsAsync(
        string ideType,
        string? sessionId = null,
        CancellationToken ct = default)
    {
        try
        {
            var breakpoints = await _debugger.ListBreakpointsAsync(ideType, sessionId, ct);
            return DebuggerApiResponse.Ok<IReadOnlyList<BreakpointInfo>>(breakpoints, $"Found {breakpoints.Count} breakpoints");
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("Invalid list breakpoints request: {Error}", ex.Message);
            return DebuggerApiResponse.Fail<IReadOnlyList<BreakpointInfo>>(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing breakpoints");
            return DebuggerApiResponse.Fail<IReadOnlyList<BreakpointInfo>>(ex.Message);
        }
    }

    public async Task<DebuggerApiResponse<ExecutionResult>> ExecuteStepAsync(
        DebuggerExecuteStepRequest request,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _debugger.ExecuteStepAsync(
                request.IdeType,
                request.Command,
                request.ThreadId,
                ct);

            return result.Success
                ? DebuggerApiResponse.Ok<ExecutionResult>(result, $"{request.Command} executed")
                : DebuggerApiResponse.Fail<ExecutionResult>(result.Message ?? "Step command failed");
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("Invalid step request: {Error}", ex.Message);
            return DebuggerApiResponse.Fail<ExecutionResult>(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing step");
            return DebuggerApiResponse.Fail<ExecutionResult>(ex.Message);
        }
    }

    public async Task<DebuggerApiResponse<DebugStateResult>> GetDebugStateAsync(
        string ideType,
        string? sessionId = null,
        CancellationToken ct = default)
    {
        try
        {
            var state = await _debugger.GetDebugStateAsync(ideType, sessionId, ct);
            return DebuggerApiResponse.Ok<DebugStateResult>(state, "Debug state retrieved");
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("Invalid get state request: {Error}", ex.Message);
            return DebuggerApiResponse.Fail<DebugStateResult>(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting debug state");
            return DebuggerApiResponse.Fail<DebugStateResult>(ex.Message);
        }
    }

    public Task<DebuggerApiResponse<IReadOnlyList<DebugSessionInfo>>> ListSessionsAsync(
        string? ideType = null,
        CancellationToken ct = default)
    {
        // This would need access to the internal session list from DebuggerControlService
        // For now, return empty as this would require exposing session management publicly
        var sessions = new List<DebugSessionInfo>().AsReadOnly();
        return Task.FromResult(
            DebuggerApiResponse.Ok<IReadOnlyList<DebugSessionInfo>>(sessions, "Sessions listed"));
    }
}
