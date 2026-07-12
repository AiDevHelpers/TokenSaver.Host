using TokenSaver.Common.Core.Contracts;

using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Text.Json;

namespace TokenSaver.MCP.Tools;

/// <summary>
///     Debug output capture and retrieval tools. Allows AI to read stdout/stderr from
///     launched processes and IDE instances for stack traces, build errors, and diagnostics.
///     See docs/debug-output-capture-contract.md.
/// </summary>
[McpServerToolType]
internal sealed class DebugOutputTools(IDebugOutputCapture outputCapture)
{
    [McpServerTool]
    [Description(
        "Retrieve captured stdout/stderr from a launched process or IDE. Returns recent output lines " +
        "with timestamps and severity levels (Info/Warning/Error/Fatal).")]
    public async Task<string> GetDebugOutput(
        [Description("Process ID to query (from LaunchIde result)")] int? processId = null,
        [Description("IDE type to query: 'VsCode' or 'VisualStudio2026' (alternative to processId)")]
        string? ideType = null,
        [Description("Maximum lines to return (default 1000, max 5000)")] int linesLimit = 1000,
        CancellationToken ct = default)
    {
        if (processId == null && ideType == null)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Must provide either processId or ideType"
            });
        }

        linesLimit = Math.Min(linesLimit, 5000);

        DebugOutput output = await outputCapture.GetCapturedOutputAsync(processId, ideType, linesLimit, ct);

        return JsonSerializer.Serialize(new
        {
            success = true,
            process_id = output.ProcessId,
            ide_type = output.IdeType,
            captured_at = output.CapturedAt.ToString("O"),
            line_count = output.Lines.Count,
            lines = output.Lines.Select(l => new
            {
                line_number = l.LineNumber,
                timestamp = l.Timestamp.ToString("HH:mm:ss.fff"),
                severity = l.Severity.ToString(),
                stream = l.Stream,
                content = l.Content
            }).ToList()
        });
    }

    [McpServerTool]
    [Description("Search captured debug output for stack traces, errors, or other patterns.")]
    public async Task<string> SearchDebugOutput(
        [Description("Regex pattern or substring to search for")] string pattern,
        [Description("Optional: limit search to specific process ID")]
        int? processId = null,
        [Description("Optional: filter by severity: 'Info', 'Warning', 'Error', 'Fatal'")]
        string? severity = null,
        CancellationToken ct = default)
    {
        DebugSeverity? severityFilter = null;
        if (!string.IsNullOrWhiteSpace(severity))
        {
            if (Enum.TryParse<DebugSeverity>(severity, ignoreCase: true, out var parsed))
            {
                severityFilter = parsed;
            }
        }

        var results = await outputCapture.SearchOutputAsync(pattern, processId, severityFilter, ct);
        var resultList = results.ToList();

        return JsonSerializer.Serialize(new
        {
            success = true,
            pattern,
            match_count = resultList.Count,
            matches = resultList.Select(l => new
            {
                line_number = l.LineNumber,
                timestamp = l.Timestamp.ToString("HH:mm:ss.fff"),
                severity = l.Severity.ToString(),
                stream = l.Stream,
                content = l.Content
            }).ToList()
        });
    }

    [McpServerTool]
    [Description("Clear captured output buffer for a process to free memory.")]
    public async Task<string> ClearDebugOutput(
        [Description("Process ID to clear")] int processId,
        CancellationToken ct = default)
    {
        await outputCapture.ClearOutputAsync(processId, ct);

        return JsonSerializer.Serialize(new
        {
            success = true,
            message = $"Cleared debug output buffer for process {processId}"
        });
    }
}
