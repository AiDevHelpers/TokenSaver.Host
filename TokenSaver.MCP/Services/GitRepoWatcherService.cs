using AiDevTools.TokenSaver.Common.Core.Processes;

using AiDevTools.TokenSaver.CodeHelper.Indexing;
using AiDevTools.TokenSaver.CodeHelper.Models.Index;
using AiDevTools.TokenSaver.Common.Core.Configuration;

using Microsoft.Extensions.Options;

namespace AiDevTools.TokenSaver.MCP.Services;

/// <summary>
///     Watches the workspace's git directory for <c>HEAD</c> and <c>index</c> writes (checkout,
///     pull, merge, commit, reset all rewrite one or both) and runs an index pass after a quiet
///     period. The pass itself is git-diff driven (see <see cref="GitStalenessService" />), so it
///     only reads files that actually changed. Worktrees and submodules resolve their own git dir
///     through <c>git rev-parse --absolute-git-dir</c>, because their <c>.git</c> is a file.
/// </summary>
internal sealed partial class GitRepoWatcherService : BackgroundService
{
    [LoggerMessage(Level = LogLevel.Information, Message = "GitRepoWatcherService watching {GitDir} for HEAD/index changes.")]
    private static partial void LogWatching(ILogger logger, string gitDir);

    [LoggerMessage(Level = LogLevel.Information, Message = "Git state changed ({Name}) — indexed {Indexed} files, pruned {Pruned}.")]
    private static partial void LogIndexed(ILogger logger, string name, int indexed, int pruned);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Git-triggered index pass failed for {Root}.")]
    private static partial void LogIndexFailed(ILogger logger, Exception ex, string root);

    // git writes HEAD and index with lock-and-rename bursts; one quiet second = one pass.
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1);

    private static readonly HashSet<string> WatchedNames = new(StringComparer.OrdinalIgnoreCase) { "HEAD", "index" };

    private readonly WorkspaceIndexService _indexService;
    private readonly TokenSaverConfig _config;
    private readonly ILogger<GitRepoWatcherService> _logger;
    private readonly Lock _sync = new();
    private CancellationTokenSource? _debounceCts;

    public override void Dispose()
    {
        _debounceCts?.Dispose();
        base.Dispose();
    }

    public GitRepoWatcherService(
        WorkspaceIndexService indexService,
        IOptions<TokenSaverConfig> config,
        ILogger<GitRepoWatcherService> logger)
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

        string? gitDir = await ResolveGitDirAsync(root, stoppingToken);
        if (gitDir == null || !Directory.Exists(gitDir))
        {
            return; // not a git workspace: nothing to watch, the mtime path handles it
        }

        using FileSystemWatcher watcher = new(gitDir)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
        };

        void OnEvent(object sender, FileSystemEventArgs e)
        {
            if (WatchedNames.Contains(e.Name ?? ""))
            {
                Schedule(root, e.Name!);
            }
        }

        watcher.Changed += OnEvent;
        watcher.Created += OnEvent;
        watcher.Renamed += (_, e) =>
        {
            if (WatchedNames.Contains(e.Name ?? ""))
            {
                Schedule(root, e.Name!);
            }
        };
        watcher.EnableRaisingEvents = true;
        LogWatching(_logger, gitDir);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private void Schedule(string root, string name)
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
                IndexStats stats = await _indexService.IndexWorkspaceAsync(root, cts.Token);
                LogIndexed(_logger, name, stats.FilesIndexed, stats.FilesPruned);
            }
            catch (OperationCanceledException)
            {
                // superseded by a later git write
            }
            catch (Exception ex)
            {
                LogIndexFailed(_logger, ex, root);
            }
        });
    }

    /// <summary>Returns the absolute git dir for <paramref name="root" />, or null when it is not a repo.</summary>
    private static async Task<string?> ResolveGitDirAsync(string root, CancellationToken ct)
    {
        CommandResult result = await CommandRunner.RunAsync(
            new CommandRequest
            {
                FileName = "git",
                Arguments = ["-C", root, "rev-parse", "--absolute-git-dir"],
                Timeout = TimeSpan.FromSeconds(10),
            },
            ct);

        // Missing git, a non-repo and a timeout all mean "no git dir".
        return result.Success ? result.StdOut.Trim() : null;
    }
}
