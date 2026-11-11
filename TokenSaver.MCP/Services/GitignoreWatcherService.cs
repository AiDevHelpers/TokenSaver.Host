using TokenSaver.CodeHelper.Indexing;
using TokenSaver.CodeHelper.Models.Index;
using TokenSaver.Common.Core.Configuration;

using Microsoft.Extensions.Options;

namespace TokenSaver.MCP.Services;

/// <summary>
///     Watches <c>.gitignore</c> plus the two <c>.tokensaver</c> ignore overrides
///     (<c>.ignore-additional</c>/<c>.ignore-bypass</c> — see <c>WorkspaceIgnoreFilter</c>) for the
///     configured workspace root, and triggers an immediate full reconcile
///     (<see cref="WorkspaceIndexService.ReconcileIgnoreChangeAsync" />) whenever any of them
///     change: newly-ignored files/folders drop out of the index, newly-un-ignored ones get added.
///     Without this, a plain <c>.gitignore</c> edit would only surface next time
///     <see cref="WorkspaceIndexService.IndexWorkspaceAsync" /> happens to run its rate-limited
///     prune pass, and newly-un-ignored files that git already tracks wouldn't be picked up at all
///     (git diff has nothing to report — only the ignore state changed, not the file).
/// </summary>
internal sealed partial class GitignoreWatcherService : BackgroundService
{
    [LoggerMessage(Level = LogLevel.Information, Message = "GitignoreWatcherService watching {Dir} for .gitignore/.tokensaver ignore-file changes.")]
    private static partial void LogWatching(ILogger logger, string dir);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not watch {Dir} for ignore-file changes.")]
    private static partial void LogWatchFailed(ILogger logger, Exception ex, string dir);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ignore rules changed ({Path}) — reconciled index: {Indexed} added/updated, {Pruned} removed.")]
    private static partial void LogReconciled(ILogger logger, string path, int indexed, int pruned);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignore-change reconcile failed for {Root}.")]
    private static partial void LogReconcileFailed(ILogger logger, Exception ex, string root);

    // FileSystemWatcher fires several events per save (write + attribute change, etc.); debounce
    // so one edit doesn't trigger several overlapping full-tree walks.
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1);

    private readonly WorkspaceIndexService _indexService;
    private readonly TokenSaverConfig _config;
    private readonly ILogger<GitignoreWatcherService> _logger;
    private readonly Lock _sync = new();
    private CancellationTokenSource? _debounceCts;

    public override void Dispose()
    {
        _debounceCts?.Dispose();
        base.Dispose();
    }

    public GitignoreWatcherService(
        WorkspaceIndexService indexService,
        IOptions<TokenSaverConfig> config,
        ILogger<GitignoreWatcherService> logger)
    {
        _indexService = indexService;
        _config = config.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string root = _config.WorkspaceRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return;
        }

        List<FileSystemWatcher> watchers = [];
        try
        {
            watchers.Add(CreateWatcher(root, ".gitignore", root));

            string localDir = Path.Combine(root, ".tokensaver");
            if (Directory.Exists(localDir))
            {
                watchers.Add(CreateWatcher(localDir, ".ignore-additional", root));
                watchers.Add(CreateWatcher(localDir, ".ignore-bypass", root));
            }

            LogWatching(_logger, root);

            // Keep the service alive for the watchers' lifetime; they do the real work via events.
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            foreach (FileSystemWatcher watcher in watchers)
            {
                watcher.Dispose();
            }
        }
    }

    private FileSystemWatcher CreateWatcher(string dir, string fileName, string workspaceRoot)
    {
        FileSystemWatcher watcher = new(dir, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
        };

        void OnEvent(object sender, FileSystemEventArgs e) => ScheduleReconcile(workspaceRoot, e.FullPath);
        watcher.Changed += OnEvent;
        watcher.Created += OnEvent;
        watcher.Deleted += OnEvent;
        watcher.Renamed += (sender, e) => ScheduleReconcile(workspaceRoot, e.FullPath);
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private void ScheduleReconcile(string root, string changedPath)
    {
        CancellationTokenSource cts;
        lock (_sync)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            cts = _debounceCts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Debounce, cts.Token);
                IndexStats stats = await _indexService.ReconcileIgnoreChangeAsync(root, cts.Token);
                LogReconciled(_logger, changedPath, stats.FilesIndexed, stats.FilesPruned);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a later change to one of the watched files.
            }
            catch (Exception ex)
            {
                LogReconcileFailed(_logger, ex, root);
            }
        });
    }
}
