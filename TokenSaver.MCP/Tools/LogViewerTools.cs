using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using TokenSaver.LogHelper.Filtering;
using TokenSaver.LogHelper.Models;
using TokenSaver.LogViewer;

namespace TokenSaver.MCP.Tools;

/// <summary>
///     MCP surface for the log viewer: opens the log being read in a Windows Terminal tab so the user can follow it too,
///     and tracks, closes and restarts those tabs.
/// </summary>
[McpServerToolType]
internal sealed class LogViewerTools
{
    private readonly LogViewerHost _viewer;

    public LogViewerTools(LogViewerHost viewer)
    {
        _viewer = viewer;
    }

    [McpServerTool]
    [Description(
        "Show a log to the user in a console tab (Windows Terminal window 'tokensaver-logview') while you read it, " +
        "so they can catch details you miss. action: start (open a tab for a channel; follows rolling logs; entries your " +
        "filter does not match are dimmed, and the user can hide them), list (watches, their tab process and state), " +
        "stop (close the tab and end the watch; id), restart (close the tab and open a new one; works for a hung viewer " +
        "and for a tab the user closed; id). match/exclude/minLevel/category are the same filters as LogQuery.")]
    public async Task<string> LogWatch(
        string action = "list",
        string? channel = null,
        string? id = null,
        string? match = null,
        string? exclude = null,
        string? minLevel = null,
        string? category = null,
        CancellationToken ct = default)
    {
        switch (action.ToUpperInvariant())
        {
            case "LIST":
                return Describe(_viewer.List());
            case "STOP":
                return id is not null && await _viewer.StopAsync(id) ? $"Stopped {id}." : $"Error: no watch '{id}'. Use action=list.";
            case "RESTART":
                return id is not null && _viewer.Restart(id) ? $"Restarting the viewer for {id}." : $"Error: no watch '{id}'. Use action=list.";
            case "START":
                break;
            default:
                return "Error: action must be start, list, stop or restart.";
        }

        if (!ChannelId.TryParse(channel, out ChannelId? channelId) || channelId is null)
        {
            return $"Error: invalid channel '{channel}'. Use LogChannels to list valid ids.";
        }

        LogSeverity? level = null;
        if (!string.IsNullOrWhiteSpace(minLevel))
        {
            if (!Enum.TryParse(minLevel, ignoreCase: true, out LogSeverity parsed) || parsed == LogSeverity.Unknown)
            {
                return $"Error: invalid minLevel '{minLevel}'. Use Trace, Debug, Information, Warning, Error or Critical.";
            }

            level = parsed;
        }

        (LogFilterSpec? filter, string? text) = BuildFilter(match, exclude, level, category);
        WatchInfo info = await _viewer.StartAsync(channelId, filter, text, reopen: true, ct);
        return Describe(new[] { info });
    }

    /// <summary>Builds the filter spec and a short text for the viewer header; null when nothing was asked.</summary>
    internal static (LogFilterSpec? Filter, string? Text) BuildFilter(string? match, string? exclude, LogSeverity? level, string? category)
    {
        if (match is null && exclude is null && level is null && category is null)
        {
            return (null, null);
        }

        var parts = new List<string>();
        if (match is not null)
        {
            parts.Add($"match={match}");
        }

        if (exclude is not null)
        {
            parts.Add($"exclude={exclude}");
        }

        if (level is not null)
        {
            parts.Add($"minLevel={level}");
        }

        if (category is not null)
        {
            parts.Add($"category={category}");
        }

        return (new LogFilterSpec { Match = match, Exclude = exclude, MinLevel = level, Category = category }, string.Join(", ", parts));
    }

    private static string Describe(IReadOnlyList<WatchInfo> watches)
    {
        if (watches.Count == 0)
        {
            return "No log viewers open.";
        }

        var sb = new StringBuilder();
        foreach (WatchInfo w in watches)
        {
            _ = sb.Append(w.Id).Append("  ").Append(w.Channel).Append("  ").Append(w.State);
            if (w.ViewerPid is { } pid)
            {
                _ = sb.Append(" (viewer pid ").Append(pid).Append(')');
            }

            if (w.Filter is not null)
            {
                _ = sb.Append("  filter: ").Append(w.Filter);
            }

            if (w.Error is not null)
            {
                _ = sb.Append("  error: ").Append(w.Error);
            }

            _ = sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }
}
