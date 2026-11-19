using System.ComponentModel;
using System.Text.Json;

using ModelContextProtocol.Server;

using TokenSaver.MCP.Services;

namespace TokenSaver.MCP.Tools;

/// <summary>
///     Lists and kills the processes this MCP server started (test runs and dev commands).
///     Processes the server did not start cannot be killed through these tools.
/// </summary>
[McpServerToolType]
internal sealed class ManagedProcessTools
{
    [McpServerTool]
    [Description("List the processes this MCP server started that are still running, with their label, age, and whether " +
                 "they respond. Use it to find hung test runs or launches.")]
    public string ProcessList()
    {
        var processes = ManagedProcessRegistry.List();
        return JsonSerializer.Serialize(new { count = processes.Count, processes });
    }

    [McpServerTool]
    [Description("Kill a process this MCP server started, and its child processes. Refuses any pid the server did not start.")]
    public string ProcessKill(
        [Description("Process id from ProcessList")] int pid)
    {
        var (ok, message) = ManagedProcessRegistry.Kill(pid);
        return JsonSerializer.Serialize(new { ok, message });
    }
}
