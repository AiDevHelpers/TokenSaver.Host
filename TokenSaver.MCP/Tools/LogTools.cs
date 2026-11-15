using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using TokenSaver.Common.Core.Configuration;
using TokenSaver.LogHelper.Contracts;
using TokenSaver.LogHelper.Models;
using TokenSaver.TokenEconomy.Contracts;

namespace TokenSaver.MCP.Tools;

/// <summary>
///     MCP surface for the log helper. Returns filtered, template-deduplicated summaries instead of raw
///     log dumps. Use <see cref="LogChannels" /> to find channel ids, then <see cref="LogQuery" /> to read one.
/// </summary>
[McpServerToolType]
internal sealed class LogTools
{
    private readonly ILogService _logs;
    private readonly ISavingsTracker _savings;
    private readonly TokenSaverConfig _config;

    public LogTools(ILogService logs, ISavingsTracker savings, IOptions<TokenSaverConfig> config)
    {
        _logs = logs;
        _savings = savings;
        _config = config.Value;
    }

    [McpServerTool]
    [Description(
        "List log channels the helper can read (files, VS Code, Visual Studio, debug sessions, system logs). " +
        "Optional name filter is a case-insensitive substring match on the channel id.")]
    public async Task<string> LogChannels(string? filter = null, CancellationToken ct = default)
    {
        IReadOnlyList<LogChannelInfo> channels = await _logs.ListChannelsAsync(filter, new LogRequestContext("mcp"), ct);
        if (channels.Count == 0)
        {
            return "No log channels found.";
        }

        var sb = new StringBuilder();
        foreach (LogChannelInfo channel in channels)
        {
            string size = channel.SizeBytes is { } bytes
                ? string.Create(CultureInfo.InvariantCulture, $" ({bytes} bytes)")
                : string.Empty;
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"{channel.Id}{size} — {channel.Description}");
        }

        return sb.ToString().TrimEnd();
    }

    [McpServerTool]
    [Description(
        "Read one log channel as a filtered summary: repeated messages collapse to counts, most severe first, " +
        "within maxTokens. Filters: match/exclude are regexes (non-backtracking; no lookaround or backreferences); " +
        "minLevel is Trace|Debug|Information|Warning|Error|Critical; category is a prefix. " +
        "Pass the returned cursor as 'since' to read only new entries next time.")]
    public async Task<string> LogQuery(
        string channel,
        string? match = null,
        string? exclude = null,
        string? minLevel = null,
        string? category = null,
        string view = "summary",
        string? since = null,
        int maxTokens = 1500,
        CancellationToken ct = default)
    {
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

        var request = new LogQueryRequest(
            channelId,
            new LogFilterSpec
            {
                Match = match,
                Exclude = exclude,
                MinLevel = level,
                Category = category,
            },
            view,
            since,
            WaitSeconds: 0,
            MaxTokens: Math.Clamp(maxTokens, 50, 8000));

        var stopwatch = Stopwatch.StartNew();
        LogQueryResult result = await _logs.QueryAsync(request, new LogRequestContext("mcp"), ct);
        stopwatch.Stop();

        if (!result.Ok)
        {
            return $"Error: {result.Error}";
        }

        await _savings.RecordAsync(
            _config.WorkspaceRoot,
            "logQuery",
            view,
            terse: true,
            rawTokens: result.RawChars / 4,
            distilledTokens: result.ReturnedChars / 4,
            durationMs: (int)stopwatch.ElapsedMilliseconds,
            ct: ct);

        var sb = new StringBuilder(result.Text);
        if (result.NextCursor is { Length: > 0 } cursor)
        {
            _ = sb.Append("\n\ncursor: ").Append(cursor);
        }

        if (result.Truncated)
        {
            _ = sb.Append("\ntruncated: true (raise maxTokens to see more)");
        }

        return sb.ToString();
    }
}
