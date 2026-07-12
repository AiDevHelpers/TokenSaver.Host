using TokenSaver.Common.Core.Contracts;

using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Text.Json;

namespace TokenSaver.MCP.Tools;

/// <summary>
///     Debugger control tools for starting/stopping debug sessions, managing breakpoints,
///     and stepping through code in VS Code and Visual Studio.
///     See docs/debugger-control-contract.md.
/// </summary>
[McpServerToolType]
internal sealed class DebuggerControlTools(IDebuggerControl debuggerControl)
{
    [McpServerTool]
    [Description(
        "Start debugging in an IDE using the default launch configuration (e.g., 'launch' in VS Code). " +
        "This is the recommended quick-start tool for debugging.")]
    public async Task<string> StartDebugger(
        [Description("IDE type: 'VsCode' or 'VisualStudio2026'")] string ideType,
        [Description("Optional JSON arguments to override defaults (e.g., {'port': 5000})")]
        string? argsJson = null,
        CancellationToken ct = default)
    {
        // Use 'launch' as the default config name
        string configName = "launch";

        IDictionary<string, object>? args = null;
        if (!string.IsNullOrWhiteSpace(argsJson))
        {
            try
            {
                args = JsonSerializer.Deserialize<Dictionary<string, object>>(argsJson);
            }
            catch (JsonException ex)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = $"Invalid JSON in args: {ex.Message}"
                });
            }
        }

        DebugSessionResult result = await debuggerControl.StartDebugSessionAsync(ideType, configName, args, ct);

        return JsonSerializer.Serialize(new
        {
            success = result.Success,
            session_id = result.SessionId,
            ide_type = result.IdeType,
            message = result.Message
        });
    }

    [McpServerTool]
    [Description("Stop debugging in an IDE. Terminates the debug session.")]
    public async Task<string> StopDebugger(
        [Description("IDE type: 'VsCode' or 'VisualStudio2026'")] string ideType,
        CancellationToken ct = default)
    {
        await debuggerControl.StopDebugSessionAsync(ideType, sessionId: null, ct);

        return JsonSerializer.Serialize(new
        {
            success = true,
            message = $"Debugging stopped for {ideType}"
        });
    }

    [McpServerTool]
    [Description(
        "Start a debug session with a specific configuration. " +
        "Use this for non-default launch configs (e.g., '.NET Core Attach', 'Python: Current File').")]
    public async Task<string> StartDebugSession(
        [Description("IDE type: 'VsCode' or 'VisualStudio2026'")] string ideType,
        [Description("Debug configuration name (e.g., 'launch', '.NET Core Attach', 'Python: Current File')")]
        string configName,
        [Description("Optional JSON arguments (e.g., {'port': 5000, 'program': 'bin/Debug/app.dll'})")]
        string? argsJson = null,
        CancellationToken ct = default)
    {
        IDictionary<string, object>? args = null;
        if (!string.IsNullOrWhiteSpace(argsJson))
        {
            try
            {
                args = JsonSerializer.Deserialize<Dictionary<string, object>>(argsJson);
            }
            catch (JsonException ex)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = $"Invalid JSON in args: {ex.Message}"
                });
            }
        }

        DebugSessionResult result = await debuggerControl.StartDebugSessionAsync(ideType, configName, args, ct);

        return JsonSerializer.Serialize(new
        {
            success = result.Success,
            session_id = result.SessionId,
            ide_type = result.IdeType,
            config_name = result.ConfigName,
            message = result.Message
        });
    }

    [McpServerTool]
    [Description("Stop an active debug session.")]
    public async Task<string> StopDebugSession(
        [Description("IDE type: 'VsCode' or 'VisualStudio2026'")] string ideType,
        [Description("Optional: specific session ID (if multiple sessions active)")] string? sessionId = null,
        CancellationToken ct = default)
    {
        await debuggerControl.StopDebugSessionAsync(ideType, sessionId, ct);

        return JsonSerializer.Serialize(new
        {
            success = true,
            message = $"Debug session stopped for {ideType}"
        });
    }

    [McpServerTool]
    [Description(
        "Set a breakpoint at a file location. The file must be open in the IDE " +
        "and part of an active debug session.")]
    public async Task<string> SetBreakpoint(
        [Description("IDE type: 'VsCode' or 'VisualStudio2026'")] string ideType,
        [Description("Absolute file path")] string filePath,
        [Description("1-indexed line number")] int lineNumber,
        [Description("Optional: breakpoint condition (e.g., 'x > 5')")] string? condition = null,
        [Description("Optional: break when hit count reaches this value")] int? hitCount = null,
        CancellationToken ct = default)
    {
        BreakpointResult result = await debuggerControl.SetBreakpointAsync(
            ideType, filePath, lineNumber, condition, hitCount, ct);

        return JsonSerializer.Serialize(new
        {
            success = result.Verified,
            breakpoint_id = result.BreakpointId,
            file_path = result.FilePath,
            line_number = result.LineNumber,
            verified = result.Verified,
            message = result.Message
        });
    }

    [McpServerTool]
    [Description("Remove a breakpoint by ID.")]
    public async Task<string> RemoveBreakpoint(
        [Description("IDE type: 'VsCode' or 'VisualStudio2026'")] string ideType,
        [Description("Breakpoint ID (from SetBreakpoint)")] string breakpointId,
        CancellationToken ct = default)
    {
        await debuggerControl.RemoveBreakpointAsync(ideType, breakpointId, ct);

        return JsonSerializer.Serialize(new
        {
            success = true,
            message = $"Breakpoint {breakpointId} removed"
        });
    }

    [McpServerTool]
    [Description("List all breakpoints in the current debug session.")]
    public async Task<string> ListBreakpoints(
        [Description("IDE type: 'VsCode' or 'VisualStudio2026'")] string ideType,
        [Description("Optional: specific session ID")] string? sessionId = null,
        CancellationToken ct = default)
    {
        IReadOnlyList<BreakpointInfo> breakpoints = await debuggerControl.ListBreakpointsAsync(ideType, sessionId, ct);

        return JsonSerializer.Serialize(new
        {
            success = true,
            breakpoint_count = breakpoints.Count,
            breakpoints = breakpoints.Select(bp => new
            {
                breakpoint_id = bp.BreakpointId,
                file_path = bp.FilePath,
                line_number = bp.LineNumber,
                verified = bp.Verified,
                condition = bp.Condition,
                hit_count = bp.HitCount,
                current_hit_count = bp.CurrentHitCount
            }).ToList()
        });
    }

    [McpServerTool]
    [Description(
        "Execute a step command in the debugger. " +
        "Commands: StepIn, StepOver, StepOut, Continue, Pause, Restart.")]
    public async Task<string> ExecuteStep(
        [Description("IDE type: 'VsCode' or 'VisualStudio2026'")] string ideType,
        [Description("Step command: 'StepIn' | 'StepOver' | 'StepOut' | 'Continue' | 'Pause' | 'Restart'")]
        string command,
        [Description("Optional: specific thread/frame ID")] string? threadId = null,
        CancellationToken ct = default)
    {
        if (!Enum.TryParse<DebugCommand>(command, ignoreCase: true, out var cmd))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = $"Unknown command: {command}. Valid commands: StepIn, StepOver, StepOut, Continue, Pause, Restart"
            });
        }

        ExecutionResult result = await debuggerControl.ExecuteStepAsync(ideType, cmd, threadId, ct);

        return JsonSerializer.Serialize(new
        {
            success = result.Success,
            command = result.Command.ToString(),
            current_file = result.CurrentFile,
            current_line = result.CurrentLine,
            reason = result.Reason,
            message = result.Message
        });
    }

    [McpServerTool]
    [Description(
        "Get the current debug state: call stack, variables, current position, etc. " +
        "Use this to inspect the program state during debugging.")]
    public async Task<string> GetDebugState(
        [Description("IDE type: 'VsCode' or 'VisualStudio2026'")] string ideType,
        [Description("Optional: specific session ID (uses first active session if not provided)")]
        string? sessionId = null,
        CancellationToken ct = default)
    {
        DebugStateResult result = await debuggerControl.GetDebugStateAsync(ideType, sessionId, ct);

        return JsonSerializer.Serialize(new
        {
            success = true,
            session_id = result.SessionId,
            is_running = result.IsRunning,
            current_file = result.CurrentFile,
            current_line = result.CurrentLine,
            call_stack = result.CallStack.Select(f => new
            {
                frame_id = f.Id,
                source = f.Source,
                line = f.Line,
                function = f.Function,
                index = f.Index
            }).ToList(),
            variables = result.Variables.Select(kvp => new
            {
                name = kvp.Key,
                value = kvp.Value.Value,
                type = kvp.Value.Type,
                variables_reference = kvp.Value.VariablesReference
            }).ToList()
        });
    }
}
