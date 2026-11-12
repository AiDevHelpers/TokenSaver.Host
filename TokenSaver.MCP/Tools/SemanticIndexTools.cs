using TokenSaver.Common.Core.Configuration;
using TokenSaver.Common.Contracts;
using TokenSaver.TokenEconomy.Models.TokenEconomy;
using TokenSaver.MCP.Services;

using Microsoft.Extensions.Options;

using ModelContextProtocol;
using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using TokenSaver.OllamaIntegration.Services;
using TokenSaver.TokenEconomy.Contracts;
using TokenSaver.TokenEconomy.Services;
using TokenSaver.Utilities.Services;

namespace TokenSaver.MCP.Tools;

/// <summary>
///     Chunk-level semantic index/search — deliberately separate from CodeHelperTools.CodeIndex
///     (which does exact/substring symbol-name search via WorkspaceIndexService). This is for
///     natural-language queries ("where do we retry a failed Ollama call") where a function/
///     section-level summary is more useful than an exact symbol name match. See
///     TokenSaver/docs/token-economy-tools-contract.md.
///     <para>
///     The workspace folder is never guessed from the server's own working directory. It comes from
///     the tool's <c>rootPath</c> argument or from the client's MCP roots; if neither is given the
///     tool returns <c>status: no_root</c> and the client has to send the path.
///     </para>
/// </summary>
[McpServerToolType]
internal sealed class SemanticIndexTools(
    IConditionalRetrievalService retrieval,
    IOptions<TokenSaverConfig> config,
    WorkspaceRootSessionResolver workspaceRootResolver,
    SemanticIndexBackgroundTrigger indexTrigger)
{
    private static readonly TimeSpan IndexWaitCap = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(15);

    private readonly TokenSaverConfig _config = config.Value;

    [McpServerTool]
    [Description(
        "Start chunking the workspace into function/class/markdown-section pieces and summarizing+embedding " +
        "each new or changed one via the local model (incremental — unchanged chunks are skipped by content " +
        "hash; pass force=true to cancel the current run, wipe all stored chunks and rebuild from scratch). Runs in the background and returns immediately; call IndexStatus to check progress. Run this " +
        "before SemanticGrep the first time, and again after significant edits. Pass rootPath (the client's " +
        "workspace folder) when the client does not send MCP roots.")]
    public async Task<string> IndexRepo(
        [Description("Workspace root to index; required unless the client sends MCP roots")]
        string? rootPath = null,
        [Description("File extensions to include, e.g. ['.cs','.md']; defaults to .cs/.js/.ts/.py/.md")]
        string[]? extensions = null,
        [Description("Max concurrent Ollama calls while indexing")]
        int concurrency = 4,
        [Description("Force a clean rebuild: cancel any run in flight, delete every stored chunk, then re-index from scratch")]
        bool force = false,
        McpServer server = null!,
        CancellationToken ct = default)
    {
        string? root = await ResolveIndexRootAsync(rootPath, server, ct);
        if (root is null)
        {
            return NoRootJson();
        }

        SemanticIndexTriggerStatus status = force
            ? await indexTrigger.ForceRestartAsync(root, extensions, concurrency, ct)
            : indexTrigger.StartManual(root, extensions, concurrency);
        return JsonSerializer.Serialize(new
        {
            status = status == SemanticIndexTriggerStatus.Started ? "started" : "already_running",
            reset = force && status == SemanticIndexTriggerStatus.Started,
            root,
            next = "Call IndexStatus to check progress."
        });
    }

    [McpServerTool]
    [Description(
        "Report the state of the background semantic index for a workspace: whether a run is in flight, " +
        "the stats of the last completed run, and the last error if it failed. Pass rootPath (the client's " +
        "workspace folder) when the client does not send MCP roots.")]
    public async Task<string> IndexStatus(
        [Description("Workspace root to check; required unless the client sends MCP roots")]
        string? rootPath = null,
        McpServer server = null!,
        CancellationToken ct = default)
    {
        string? root = await ResolveIndexRootAsync(rootPath, server, ct);
        if (root is null)
        {
            return NoRootJson();
        }

        bool running = indexTrigger.IsRunning(root);
        string? error = indexTrigger.GetLastError(root);
        // Read from the database, not process memory: a restarted server still has chunks on disk.
        int chunksStored = 0;
        long? lastUpdatedMs = null;
        try
        {
            (chunksStored, lastUpdatedMs) = await indexTrigger.ChunkSummaryAsync(root, ct);
        }
        catch (Exception ex) when (IndexSchemaGuard.IsSchemaMismatch(ex))
        {
            return IndexSchemaGuard.NeedsRebuildJson(root, ex);
        }
        object? last = null;
        if (indexTrigger.TryGetLastStats(root, out RepoIndexStats? stats) && stats is not null)
        {
            last = new
            {
                files = stats.FilesScanned,
                chunks_indexed = stats.ChunksIndexed,
                chunks_skipped = stats.ChunksSkipped,
                chunks_deleted = stats.ChunksDeleted,
                chunks_failed = stats.ChunksFailed,
                elapsed_seconds = Math.Round(stats.Elapsed.TotalSeconds, 1)
            };
        }

        return JsonSerializer.Serialize(new
        {
            type = "semantic",
            root,
            chunks = chunksStored,
            status = running ? "running"
                : indexTrigger.NeedsRebuild(root) ? "needs_rebuild"
                : chunksStored > 0 ? "built"
                : error is not null ? "failed"
                : "not_started",
            date = lastUpdatedMs is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("o") : null,
            last_completed = last,
            last_error = error,
            rebuild = indexTrigger.NeedsRebuild(root) ? IndexSchemaGuard.RebuildHint : null
        });
    }

    [McpServerTool]
    [Description(
        "Natural-language search over the chunk index built by IndexRepo. Returns file/lines/summary/score per " +
        "hit — not raw code; read the actual file only for the 1-2 hits that look relevant. If the index for " +
        "this workspace has not been built yet, the call starts the build and waits for it, sending progress " +
        "notifications while it runs. Ambiguous queries are silently reranked by a second local model pass " +
        "before ranking; if the best hit still scores below the confidence floor, returns " +
        "{\"match\":\"none\", best_candidate, confidence} instead — treat that as \"nothing relevant is " +
        "indexed for this,\" not as a prompt to use best_candidate anyway. Pass rootPath (the client's " +
        "workspace folder) when the client does not send MCP roots.")]
    public async Task<string> SemanticGrep(
        [Description("Natural-language query")] string query,
        [Description("Optional substring filter on the relative file path")]
        string? pathFilter = null,
        [Description("Number of top results to return (default 5)")]
        int topK = 5,
        [Description("Workspace root to search; required unless the client sends MCP roots")]
        string? rootPath = null,
        McpServer server = null!,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken ct = default)
    {
        string? root = await ResolveIndexRootAsync(rootPath, server, ct);
        if (root is null)
        {
            return NoRootJson();
        }

        string? waitResult;
        RetrievalOutcome outcome;
        try
        {
            waitResult = await WaitForIndexAsync(root, progress, ct);
            if (waitResult is not null)
            {
                return waitResult;
            }

            outcome = await retrieval.RetrieveAsync(root, query, pathFilter, topK, ct);
        }
        catch (Exception ex) when (IndexSchemaGuard.IsSchemaMismatch(ex))
        {
            return IndexSchemaGuard.NeedsRebuildJson(root, ex);
        }


        if (outcome.Abstained)
        {
            return JsonSerializer.Serialize(new
            {
                match = "none",
                best_candidate = ToHitJson(outcome.BestCandidate!),
                confidence = outcome.Confidence
            });
        }

        return JsonSerializer.Serialize(outcome.Hits.Select(ToHitJson));
    }

    /// <summary>
    ///     Returns the workspace root for an index tool call: the explicit <paramref name="rootPath" />
    ///     if given, otherwise the root the client supplied through MCP roots. Never the server's own
    ///     working directory. Null when neither is available.
    /// </summary>
    private async Task<string?> ResolveIndexRootAsync(string? rootPath, McpServer server, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(rootPath))
        {
            await workspaceRootResolver.EnsureResolvedAsync(server, rootPath, ct);
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        }

        await workspaceRootResolver.EnsureResolvedAsync(server, ct);
        return string.IsNullOrWhiteSpace(_config.WorkspaceRoot) ? null : _config.WorkspaceRoot;
    }

    /// <summary>
    ///     Waits while the index for <paramref name="root" /> has no chunks yet: starts the build if
    ///     nothing is running, reports progress every <see cref="ProgressInterval" />, and returns a
    ///     JSON status when the wait ends without an index. Returns null when the search can proceed.
    /// </summary>
    private async Task<string?> WaitForIndexAsync(
        string root, IProgress<ProgressNotificationValue>? progress, CancellationToken ct)
    {
        int chunks = await indexTrigger.CountChunksAsync(root, ct);
        if (chunks > 0)
        {
            return null;
        }

        if (!indexTrigger.IsRunning(root))
        {
            _ = indexTrigger.StartManual(root, null, 4);
        }

        Stopwatch sw = Stopwatch.StartNew();
        TimeSpan nextReport = TimeSpan.Zero;
        while (indexTrigger.IsRunning(root))
        {
            if (sw.Elapsed >= IndexWaitCap)
            {
                return JsonSerializer.Serialize(new
                {
                    status = "indexing",
                    root,
                    message = "The index is still being built. Call SemanticGrep again to keep waiting."
                });
            }

            if (sw.Elapsed >= nextReport)
            {
                chunks = await indexTrigger.CountChunksAsync(root, ct);
                ReportProgress(progress, chunks, sw.Elapsed);
                nextReport += ProgressInterval;
            }

            await Task.Delay(PollInterval, ct);
        }

        chunks = await indexTrigger.CountChunksAsync(root, ct);
        if (chunks > 0)
        {
            return null;
        }

        string? error = indexTrigger.GetLastError(root);
        if (error is null)
        {
            // Finished without error but still empty: the search runs and returns no hits.
            return null;
        }

        return JsonSerializer.Serialize(new
        {
            status = "failed",
            root,
            chunks,
            last_error = error,
            message = "The index build failed. Fix the cause and call IndexRepo again."
        });
    }

    private static void ReportProgress(IProgress<ProgressNotificationValue>? progress, int chunks, TimeSpan elapsed)
    {
        progress?.Report(new ProgressNotificationValue
        {
            Progress = chunks,
            Message = $"Building the semantic index: {chunks} chunks stored, {elapsed:mm\\:ss} elapsed"
        });
    }

    private static string NoRootJson() => JsonSerializer.Serialize(new
    {
        status = "no_root",
        message = "The client did not send a workspace folder. Call again with rootPath set to the folder to index."
    });

    private static object ToHitJson(SemanticSearchHit h) => new
    {
        file = h.RelativePath,
        lines = $"{h.StartLine}-{h.EndLine}",
        type = h.ChunkType,
        name = h.Name,
        summary = h.Summary,
        score = h.Score
    };
}
