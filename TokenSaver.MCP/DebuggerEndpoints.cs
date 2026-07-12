using TokenSaver.MCP.Services;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace TokenSaver.MCP;

/// <summary>
/// HTTP endpoint mappings for the debugger API.
/// Provides /api/debugger/* endpoints for VS Code debugger extension communication.
/// </summary>
internal static class DebuggerEndpoints
{
    /// <summary>Map debugger API endpoints to the application.</summary>
    public static WebApplication MapDebuggerEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/debugger");

        // POST /api/debugger/sessions - Start a debug session
        group.MapPost("/sessions", StartDebugSession);

        // DELETE /api/debugger/sessions/{sessionId} - Stop a debug session
        group.MapDelete("/sessions/{sessionId}", StopDebugSession);

        // POST /api/debugger/breakpoints - Set a breakpoint
        group.MapPost("/breakpoints", SetBreakpoint);

        // DELETE /api/debugger/breakpoints/{breakpointId} - Remove a breakpoint
        group.MapDelete("/breakpoints/{breakpointId}", RemoveBreakpoint);

        // GET /api/debugger/breakpoints - List breakpoints
        group.MapGet("/breakpoints", ListBreakpoints);

        // POST /api/debugger/step - Execute a step command
        group.MapPost("/step", ExecuteStep);

        // GET /api/debugger/state - Get current debug state
        group.MapGet("/state", GetDebugState);

        // GET /api/debugger/sessions - List active sessions
        group.MapGet("/sessions", ListSessions);

        return app;
    }

    // ── Endpoint Handlers ──

    private static async Task<IResult> StartDebugSession(
        IDebuggerApiClient debugger,
        DebuggerStartSessionRequest request,
        CancellationToken ct)
    {
        var response = await debugger.StartSessionAsync(request, ct);
        return response.Success
            ? Results.Ok(response)
            : Results.BadRequest(response);
    }

    private static async Task<IResult> StopDebugSession(
        IDebuggerApiClient debugger,
        string sessionId,
        string ideType,
        CancellationToken ct)
    {
        var response = await debugger.StopSessionAsync(ideType, sessionId, ct);
        return response.Success
            ? Results.Ok(response)
            : Results.BadRequest(response);
    }

    private static async Task<IResult> SetBreakpoint(
        IDebuggerApiClient debugger,
        DebuggerSetBreakpointRequest request,
        CancellationToken ct)
    {
        var response = await debugger.SetBreakpointAsync(request, ct);
        return response.Success
            ? Results.Ok(response)
            : Results.BadRequest(response);
    }

    private static async Task<IResult> RemoveBreakpoint(
        IDebuggerApiClient debugger,
        string breakpointId,
        string ideType,
        CancellationToken ct)
    {
        var response = await debugger.RemoveBreakpointAsync(ideType, breakpointId, ct);
        return response.Success
            ? Results.Ok(response)
            : Results.BadRequest(response);
    }

    private static async Task<IResult> ListBreakpoints(
        IDebuggerApiClient debugger,
        string ideType,
        string? sessionId,
        CancellationToken ct)
    {
        var response = await debugger.ListBreakpointsAsync(ideType, sessionId, ct);
        return response.Success
            ? Results.Ok(response)
            : Results.BadRequest(response);
    }

    private static async Task<IResult> ExecuteStep(
        IDebuggerApiClient debugger,
        DebuggerExecuteStepRequest request,
        CancellationToken ct)
    {
        var response = await debugger.ExecuteStepAsync(request, ct);
        return response.Success
            ? Results.Ok(response)
            : Results.BadRequest(response);
    }

    private static async Task<IResult> GetDebugState(
        IDebuggerApiClient debugger,
        string ideType,
        string? sessionId,
        CancellationToken ct)
    {
        var response = await debugger.GetDebugStateAsync(ideType, sessionId, ct);
        return response.Success
            ? Results.Ok(response)
            : Results.BadRequest(response);
    }

    private static async Task<IResult> ListSessions(
        IDebuggerApiClient debugger,
        string? ideType,
        CancellationToken ct)
    {
        var response = await debugger.ListSessionsAsync(ideType, ct);
        return response.Success
            ? Results.Ok(response)
            : Results.BadRequest(response);
    }
}
