using TokenSaver.Common.Core.Contracts;
using TokenSaver.MCP.Tools;

using System.Text.Json;

using Xunit;

namespace TokenSaver.MCP.Tests.Tools;

/// <summary>
///     Covers DebuggerControlTools through a fake IDebuggerControl. No IDE or debugger is started.
/// </summary>
public sealed class DebuggerControlToolsTests
{
    [Fact]
    public async Task StartDebugger_ValidArgsJson_UsesLaunchConfigAndForwardsArgs()
    {
        FakeDebuggerControl control = new()
        {
            StartResult = new DebugSessionResult("s-1", "VsCode", Success: true, Message: "started", ConfigName: "launch"),
        };
        DebuggerControlTools tools = new(control);

        string json = await tools.StartDebugger("VsCode", argsJson: "{\"port\": 5000}");
        JsonElement result = JsonElement.Parse(json);

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal("s-1", result.GetProperty("session_id").GetString());
        Assert.Equal("launch", control.LastConfigName);
        Assert.NotNull(control.LastArgs);
        Assert.True(control.LastArgs!.ContainsKey("port"));
    }

    [Fact]
    public async Task StartDebugger_InvalidArgsJson_ReturnsErrorWithoutStarting()
    {
        FakeDebuggerControl control = new();
        DebuggerControlTools tools = new(control);

        string json = await tools.StartDebugger("VsCode", argsJson: "{not json");
        JsonElement result = JsonElement.Parse(json);

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.StartsWith("Invalid JSON in args:", result.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, control.StartCalls);
    }

    [Fact]
    public async Task StopDebugger_ValidIde_StopsWithoutSessionId()
    {
        FakeDebuggerControl control = new();
        DebuggerControlTools tools = new(control);

        string json = await tools.StopDebugger("VisualStudio2026");
        JsonElement result = JsonElement.Parse(json);

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal("Debugging stopped for VisualStudio2026", result.GetProperty("message").GetString());
        Assert.Equal("VisualStudio2026", control.LastStopIdeType);
        Assert.Null(control.LastStopSessionId);
    }

    [Fact]
    public async Task StartDebugSession_NamedConfig_PassesConfigName()
    {
        FakeDebuggerControl control = new()
        {
            StartResult = new DebugSessionResult("s-2", "VsCode", Success: true, Message: "ok", ConfigName: ".NET Core Attach"),
        };
        DebuggerControlTools tools = new(control);

        string json = await tools.StartDebugSession("VsCode", ".NET Core Attach");
        JsonElement result = JsonElement.Parse(json);

        Assert.Equal(".NET Core Attach", result.GetProperty("config_name").GetString());
        Assert.Equal(".NET Core Attach", control.LastConfigName);
        Assert.Null(control.LastArgs);
    }

    [Fact]
    public async Task StartDebugSession_InvalidArgsJson_ReturnsErrorWithoutStarting()
    {
        FakeDebuggerControl control = new();
        DebuggerControlTools tools = new(control);

        string json = await tools.StartDebugSession("VsCode", "launch", argsJson: "[");
        JsonElement result = JsonElement.Parse(json);

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal(0, control.StartCalls);
    }

    [Fact]
    public async Task StopDebugSession_SessionIdGiven_PassesSessionId()
    {
        FakeDebuggerControl control = new();
        DebuggerControlTools tools = new(control);

        string json = await tools.StopDebugSession("VsCode", sessionId: "s-9");
        JsonElement result = JsonElement.Parse(json);

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal("s-9", control.LastStopSessionId);
    }

    [Fact]
    public async Task SetBreakpoint_VerifiedBreakpoint_ReturnsIdAndSuccess()
    {
        FakeDebuggerControl control = new()
        {
            BreakpointResult = new BreakpointResult("bp-1", "F:\\app\\Program.cs", 12, Verified: true, Message: "set"),
        };
        DebuggerControlTools tools = new(control);

        string json = await tools.SetBreakpoint("VsCode", "F:\\app\\Program.cs", 12, condition: "x > 5", hitCount: 3);
        JsonElement result = JsonElement.Parse(json);

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal("bp-1", result.GetProperty("breakpoint_id").GetString());
        Assert.Equal(12, result.GetProperty("line_number").GetInt32());
        Assert.Equal("x > 5", control.LastCondition);
        Assert.Equal(3, control.LastHitCount);
    }

    [Fact]
    public async Task RemoveBreakpoint_KnownId_ReportsRemoved()
    {
        FakeDebuggerControl control = new();
        DebuggerControlTools tools = new(control);

        string json = await tools.RemoveBreakpoint("VsCode", "bp-1");
        JsonElement result = JsonElement.Parse(json);

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal("Breakpoint bp-1 removed", result.GetProperty("message").GetString());
        Assert.Equal("bp-1", control.LastRemovedBreakpointId);
    }

    [Fact]
    public async Task ListBreakpoints_TwoBreakpoints_ReturnsCountAndDetails()
    {
        FakeDebuggerControl control = new()
        {
            Breakpoints =
            [
                new BreakpointInfo("bp-1", "a.cs", 1, Verified: true),
                new BreakpointInfo("bp-2", "b.cs", 2, Verified: false, Condition: "y", HitCount: 4, CurrentHitCount: 1),
            ],
        };
        DebuggerControlTools tools = new(control);

        string json = await tools.ListBreakpoints("VsCode");
        JsonElement result = JsonElement.Parse(json);

        Assert.Equal(2, result.GetProperty("breakpoint_count").GetInt32());
        JsonElement second = result.GetProperty("breakpoints")[1];
        Assert.Equal("bp-2", second.GetProperty("breakpoint_id").GetString());
        Assert.Equal("y", second.GetProperty("condition").GetString());
        Assert.Equal(1, second.GetProperty("current_hit_count").GetInt32());
    }

    [Fact]
    public async Task ExecuteStep_UnknownCommand_ReturnsErrorWithoutStepping()
    {
        FakeDebuggerControl control = new();
        DebuggerControlTools tools = new(control);

        string json = await tools.ExecuteStep("VsCode", "Teleport");
        JsonElement result = JsonElement.Parse(json);

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.StartsWith("Unknown command: Teleport.", result.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, control.StepCalls);
    }

    [Fact]
    public async Task ExecuteStep_StepOverCaseInsensitive_ForwardsParsedCommand()
    {
        FakeDebuggerControl control = new()
        {
            StepResult = new ExecutionResult(Success: true, Command: DebugCommand.StepOver, CurrentFile: "a.cs", CurrentLine: 5, Reason: "step"),
        };
        DebuggerControlTools tools = new(control);

        string json = await tools.ExecuteStep("VsCode", "stepover");
        JsonElement result = JsonElement.Parse(json);

        Assert.Equal(DebugCommand.StepOver, control.LastCommand);
        Assert.Equal("StepOver", result.GetProperty("command").GetString());
        Assert.Equal(5, result.GetProperty("current_line").GetInt32());
    }

    [Fact]
    public async Task GetDebugState_ReturnsStackAndVariables()
    {
        FakeDebuggerControl control = new()
        {
            State = new DebugStateResult(
                SessionId: "s-3",
                IsRunning: false,
                CurrentFile: "a.cs",
                CurrentLine: 9,
                CallStack: [new StackFrame("f-1", "a.cs", 9, "Main", 0)],
                Variables: new Dictionary<string, Variable> { ["count"] = new Variable("count", "3", "int", 0) }),
        };
        DebuggerControlTools tools = new(control);

        string json = await tools.GetDebugState("VsCode");
        JsonElement result = JsonElement.Parse(json);

        Assert.Equal("s-3", result.GetProperty("session_id").GetString());
        Assert.False(result.GetProperty("is_running").GetBoolean());
        Assert.Equal("Main", result.GetProperty("call_stack")[0].GetProperty("function").GetString());
        Assert.Equal("3", result.GetProperty("variables")[0].GetProperty("value").GetString());
    }

    private sealed class FakeDebuggerControl : IDebuggerControl
    {
        public DebugSessionResult StartResult { get; set; } = new("s-0", "VsCode", true, "ok");
        public BreakpointResult BreakpointResult { get; set; } = new("bp-0", "a.cs", 1, true, "ok");
        public IReadOnlyList<BreakpointInfo> Breakpoints { get; set; } = [];
        public ExecutionResult StepResult { get; set; } = new(true, DebugCommand.Continue);
        public DebugStateResult State { get; set; } = new("s-0", false, null, null, [], new Dictionary<string, Variable>());

        public int StartCalls { get; private set; }
        public int StepCalls { get; private set; }
        public string? LastConfigName { get; private set; }
        public IDictionary<string, object>? LastArgs { get; private set; }
        public string? LastStopIdeType { get; private set; }
        public string? LastStopSessionId { get; private set; }
        public string? LastCondition { get; private set; }
        public int? LastHitCount { get; private set; }
        public string? LastRemovedBreakpointId { get; private set; }
        public DebugCommand LastCommand { get; private set; }

        public Task<DebugSessionResult> StartDebugSessionAsync(string ideType, string configName, IDictionary<string, object>? args = null, CancellationToken ct = default)
        {
            StartCalls++;
            LastConfigName = configName;
            LastArgs = args;
            return Task.FromResult(StartResult);
        }

        public Task StopDebugSessionAsync(string ideType, string? sessionId = null, CancellationToken ct = default)
        {
            LastStopIdeType = ideType;
            LastStopSessionId = sessionId;
            return Task.CompletedTask;
        }

        public Task<BreakpointResult> SetBreakpointAsync(string ideType, string filePath, int lineNumber, string? condition = null, int? hitCount = null, CancellationToken ct = default)
        {
            LastCondition = condition;
            LastHitCount = hitCount;
            return Task.FromResult(BreakpointResult);
        }

        public Task RemoveBreakpointAsync(string ideType, string breakpointId, CancellationToken ct = default)
        {
            LastRemovedBreakpointId = breakpointId;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<BreakpointInfo>> ListBreakpointsAsync(string ideType, string? sessionId = null, CancellationToken ct = default) =>
            Task.FromResult(Breakpoints);

        public Task<ExecutionResult> ExecuteStepAsync(string ideType, DebugCommand command, string? threadId = null, CancellationToken ct = default)
        {
            StepCalls++;
            LastCommand = command;
            return Task.FromResult(StepResult);
        }

        public Task<DebugStateResult> GetDebugStateAsync(string ideType, string? sessionId = null, CancellationToken ct = default) =>
            Task.FromResult(State);
    }
}
