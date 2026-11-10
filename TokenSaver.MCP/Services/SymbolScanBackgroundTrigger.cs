using TokenSaver.CodeHelper.Indexing;
using TokenSaver.CodeHelper.Models.Index;

using System.Collections.Concurrent;

namespace TokenSaver.MCP.Services;

/// <summary>
///     Runs the workspace symbol scan (CodeIndex 'scan') detached from the calling MCP request. A scan
///     that runs inside the request is cancelled when the client's MCP idle timeout fires (about 300 s),
///     which is what happened to a 352 s scan. The request returns at once; CodeIndex 'status' reports
///     progress. Mirrors SemanticIndexBackgroundTrigger.
/// </summary>
public sealed partial class SymbolScanBackgroundTrigger(
    WorkspaceIndexService index, ILogger<SymbolScanBackgroundTrigger> logger)
{
    private readonly ConcurrentDictionary<string, byte> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IndexStats> _lastStats = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastCompletedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _lastError = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Starts a scan for <paramref name="root" />. False when one is already in flight.</summary>
    public bool StartScan(string root)
    {
        if (!_running.TryAdd(root, 0))
        {
            return false;
        }

        _ = _lastError.TryRemove(root, out _);
        LogStarted(logger, root);
        _ = Task.Run(() => RunAsync(root));
        return true;
    }

    public bool IsRunning(string root) => _running.ContainsKey(root);

    public bool TryGetLastStats(string root, out IndexStats? stats)
    {
        bool found = _lastStats.TryGetValue(root, out IndexStats? value);
        stats = value;
        return found;
    }

    public string? GetLastError(string root) => _lastError.TryGetValue(root, out string? message) ? message : null;

    /// <summary>When the most recent successful scan for this root finished (UTC), if any.</summary>
    public DateTimeOffset? GetLastCompletedAt(string root) =>
        _lastCompletedAt.TryGetValue(root, out DateTimeOffset at) ? at : null;

    private async Task RunAsync(string root)
    {
        try
        {
            IndexStats stats = await index.IndexWorkspaceAsync(root, CancellationToken.None);
            _lastStats[root] = stats;
            _lastCompletedAt[root] = DateTimeOffset.UtcNow;
            LogCompleted(logger, root, stats.FilesIndexed, stats.SymbolsIndexed, stats.FilesSkipped, stats.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            _lastError[root] = ex.Message;
            LogFailed(logger, root, ex);
        }
        finally
        {
            _ = _running.TryRemove(root, out _);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Background symbol scan for '{Root}' started.")]
    private static partial void LogStarted(ILogger logger, string root);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Background symbol scan for '{Root}' complete: {Files} files indexed, {Symbols} symbols, {Skipped} unchanged, {Seconds:F1}s.")]
    private static partial void LogCompleted(ILogger logger, string root, int files, int symbols, int skipped, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Background symbol scan for '{Root}' failed.")]
    private static partial void LogFailed(ILogger logger, string root, Exception ex);
}
