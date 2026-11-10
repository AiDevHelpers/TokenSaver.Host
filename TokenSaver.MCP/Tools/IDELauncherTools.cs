using TokenSaver.Common.Core.Configuration;
using TokenSaver.Common.Core.Contracts;
using TokenSaver.MCP.Services;

using Microsoft.Extensions.Options;

using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Text.Json;

namespace TokenSaver.MCP.Tools;

/// <summary>
///     IDE launch and control tools. Allows the MCP to open VS Code, Visual Studio, or future IDEs
///     with specified workspace roots, query their status, and manage running instances.
///     See docs/ide-launcher-contract.md.
/// </summary>
[McpServerToolType]
internal sealed class IDELauncherTools(
    IIDELauncher ideLauncher,
    IOptions<TokenSaverConfig> config,
    WorkspaceRootSessionResolver workspaceRootResolver)
{
    [McpServerTool]
    [Description(
        "Launch an IDE instance (Visual Studio Code, Visual Studio 2026, etc.) with the specified workspace root. " +
        "If the IDE is already running, returns its existing process info.")]
    public async Task<string> LaunchIde(
        [Description("IDE type: 'VsCode' or 'VisualStudio2026'")] string ideType,
        [Description("Absolute path to the workspace root to open (defaults to current workspace root)")]
        string? workspaceRoot = null,
        [Description("If false, return immediately without waiting for IDE readiness (default: true)")]
        bool waitForReady = true,
        McpServer server = null!,
        CancellationToken ct = default)
    {
        await workspaceRootResolver.EnsureResolvedAsync(server, ct);

        // No working-directory fallback: the IDE must open a folder the client actually named.
        string? root = workspaceRoot ?? config.Value.WorkspaceRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                status = "no_root",
                message = "The client did not send a workspace folder. Pass workspaceRoot to choose one."
            });
        }

        IDELaunchResult result = await ideLauncher.LaunchAsync(ideType, root, waitForReady, ct);

        return JsonSerializer.Serialize(new
        {
            success = true,
            ide_type = ideType,
            process_id = result.ProcessId,
            port = result.Port,
            ready = result.Ready,
            message = result.Message,
            metadata = result.Metadata
        });
    }

    [McpServerTool]
    [Description("Query the status of a running IDE instance.")]
    public async Task<string> QueryIDEStatus(
        [Description("IDE type to query: 'VsCode' or 'VisualStudio2026'")]
        string ideType,
        CancellationToken ct = default)
    {
        IDEStatusResult result = await ideLauncher.QueryStatusAsync(ideType, ct);

        return JsonSerializer.Serialize(new
        {
            ide_type = result.IdeType,
            is_running = result.IsRunning,
            process_id = result.ProcessId,
            port = result.Port,
            ready = result.Ready,
            workspace_root = result.WorkspaceRoot
        });
    }

    [McpServerTool]
    [Description("Stop a running IDE instance gracefully or forcefully.")]
    public async Task<string> StopIDE(
        [Description("IDE type to stop: 'VsCode' or 'VisualStudio2026'")]
        string ideType,
        [Description("If true, request graceful shutdown; if false, force-kill (default: true)")]
        bool graceful = true,
        CancellationToken ct = default)
    {
        await ideLauncher.StopAsync(ideType, graceful, ct);

        return JsonSerializer.Serialize(new
        {
            success = true,
            message = $"{ideType} stop requested"
        });
    }
}
