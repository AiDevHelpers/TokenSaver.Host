using TokenSaver.Common.Core.Configuration;
using TokenSaver.Common.Core.Contracts;
using TokenSaver.MCP.Tools;

using Microsoft.Extensions.Options;

using System.Text.Json;

using Xunit;

namespace TokenSaver.MCP.Tests.Tools;

/// <summary>
///     Covers IDELauncherTools through a fake IIDELauncher. No IDE is launched, queried or stopped.
/// </summary>
public sealed class IDELauncherToolsTests
{
    private static IDELauncherTools CreateTools(FakeIdeLauncher launcher, string workspaceRoot = "F:\\workspace")
    {
        TokenSaverConfig cfg = new() { WorkspaceRoot = workspaceRoot };
        return new IDELauncherTools(
            launcher,
            Options.Create(cfg),
            WorkspaceRootSessionResolverTestFactory.Create(cfg));
    }

    [Fact]
    public async Task LaunchIde_ExplicitRoot_PassesRootToLauncherAndReportsResult()
    {
        FakeIdeLauncher launcher = new()
        {
            LaunchResult = new IDELaunchResult(ProcessId: 900, Port: 5678, Ready: true, Message: "started"),
        };
        IDELauncherTools tools = CreateTools(launcher);

        string json = await tools.LaunchIde("VsCode", workspaceRoot: "F:\\other", waitForReady: false);
        JsonElement result = JsonElement.Parse(json);

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal(900, result.GetProperty("process_id").GetInt32());
        Assert.Equal(5678, result.GetProperty("port").GetInt32());
        Assert.True(result.GetProperty("ready").GetBoolean());
        Assert.Equal("started", result.GetProperty("message").GetString());
        Assert.Equal("VsCode", launcher.LastLaunchIdeType);
        Assert.Equal("F:\\other", launcher.LastLaunchRoot);
        Assert.False(launcher.LastWaitForReady);
    }

    [Fact]
    public async Task LaunchIde_NoExplicitRoot_UsesConfiguredWorkspaceRoot()
    {
        FakeIdeLauncher launcher = new()
        {
            LaunchResult = new IDELaunchResult(ProcessId: 1, Port: null, Ready: false, Message: "waiting"),
        };
        IDELauncherTools tools = CreateTools(launcher, workspaceRoot: "F:\\configured");

        string json = await tools.LaunchIde("VisualStudio2026");
        JsonElement result = JsonElement.Parse(json);

        Assert.Equal("F:\\configured", launcher.LastLaunchRoot);
        Assert.False(result.GetProperty("ready").GetBoolean());
        Assert.Equal("VisualStudio2026", result.GetProperty("ide_type").GetString());
    }

    [Fact]
    public async Task QueryIDEStatus_RunningIde_ReportsStatusFields()
    {
        FakeIdeLauncher launcher = new()
        {
            StatusResult = new IDEStatusResult("VsCode", IsRunning: true, ProcessId: 77, Port: 9000, Ready: true, WorkspaceRoot: "F:\\ws"),
        };
        IDELauncherTools tools = CreateTools(launcher);

        string json = await tools.QueryIDEStatus("VsCode");
        JsonElement result = JsonElement.Parse(json);

        Assert.True(result.GetProperty("is_running").GetBoolean());
        Assert.Equal(77, result.GetProperty("process_id").GetInt32());
        Assert.Equal("F:\\ws", result.GetProperty("workspace_root").GetString());
        Assert.Equal("VsCode", launcher.LastStatusIdeType);
    }

    [Fact]
    public async Task StopIDE_GracefulDefault_RequestsGracefulStop()
    {
        FakeIdeLauncher launcher = new();
        IDELauncherTools tools = CreateTools(launcher);

        string json = await tools.StopIDE("VsCode");
        JsonElement result = JsonElement.Parse(json);

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal("VsCode stop requested", result.GetProperty("message").GetString());
        Assert.True(launcher.LastStopGraceful);
    }

    [Fact]
    public async Task StopIDE_ForceRequested_PassesGracefulFalse()
    {
        FakeIdeLauncher launcher = new();
        IDELauncherTools tools = CreateTools(launcher);

        _ = await tools.StopIDE("VisualStudio2026", graceful: false);

        Assert.False(launcher.LastStopGraceful);
        Assert.Equal("VisualStudio2026", launcher.LastStopIdeType);
    }

    private sealed class FakeIdeLauncher : IIDELauncher
    {
        public IDELaunchResult LaunchResult { get; set; } = new(0, null, false, "");
        public IDEStatusResult StatusResult { get; set; } = new("VsCode", false, null, null, false);
        public string? LastLaunchIdeType { get; private set; }
        public string? LastLaunchRoot { get; private set; }
        public bool LastWaitForReady { get; private set; }
        public string? LastStatusIdeType { get; private set; }
        public string? LastStopIdeType { get; private set; }
        public bool LastStopGraceful { get; private set; }

        public Task<IDELaunchResult> LaunchAsync(string ideType, string workspaceRoot, bool waitForReady = true, CancellationToken ct = default)
        {
            LastLaunchIdeType = ideType;
            LastLaunchRoot = workspaceRoot;
            LastWaitForReady = waitForReady;
            return Task.FromResult(LaunchResult);
        }

        public Task<IDEStatusResult> QueryStatusAsync(string ideType, CancellationToken ct = default)
        {
            LastStatusIdeType = ideType;
            return Task.FromResult(StatusResult);
        }

        public Task StopAsync(string ideType, bool graceful = true, CancellationToken ct = default)
        {
            LastStopIdeType = ideType;
            LastStopGraceful = graceful;
            return Task.CompletedTask;
        }
    }
}
