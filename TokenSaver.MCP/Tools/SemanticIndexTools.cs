using TokenSaver.Common.Core.Configuration;
using TokenSaver.Common.Contracts;
using TokenSaver.TokenEconomy.Models.TokenEconomy;
using TokenSaver.MCP.Services;

using Microsoft.Extensions.Options;

using ModelContextProtocol.Server;

using System.ComponentModel;
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
/// </summary>
[McpServerToolType]
internal sealed class SemanticIndexTools(
    IRepoIndexer indexer,
    IConditionalRetrievalService retrieval,
    IOptions<TokenSaverConfig> config,
    WorkspaceRootSessionResolver workspaceRootResolver,
    SemanticIndexBackgroundTrigger indexTrigger)
{
    private readonly TokenSaverConfig _config = config.Value;

    [McpServerTool]
    [Description(
        "Start chunking the workspace into function/class/markdown-section pieces and summarizing+embedding " +
        "each new or changed one via the local model (incremental — unchanged chunks are skipped by content " +
        "hash). Runs in the background and returns immediately; call IndexStatus to check progress. Run this " +
        "before SemanticGrep the first time, and again after significant edits.")]
    public async Task<string> IndexRepo(
        [Description("Workspace root to index; defaults to the configured WorkspaceRoot")]
        string? rootPath = null,
        [Description("File extensions to include, e.g. ['.cs','.md']; defaults to .cs/.js/.ts/.py/.md")]
        string[]? extensions = null,
        [Description("Max concurrent Ollama calls while indexing")]
        int concurrency = 4,
        McpServer server = null!,
        CancellationToken ct = default)
    {
        await workspaceRootResolver.EnsureResolvedAsync(server, ct);

        string root = string.IsNullOrWhiteSpace(rootPath) ? _config.WorkspaceRoot : rootPath;
        SemanticIndexTriggerStatus status = indexTrigger.StartManual(root, extensions, concurrency);
        return JsonSerializer.Serialize(new
        {
            status = status == SemanticIndexTriggerStatus.Started ? "started" : "already_running",
            root,
            next = "Call IndexStatus to check progress."
        });
    }

    [McpServerTool]
    [Description(
        "Report the state of the background semantic index for a workspace: whether a run is in flight, " +
        "the stats of the last completed run, and the last error if it failed.")]
    public Task<string> IndexStatus(
        [Description("Workspace root to check; defaults to the configured WorkspaceRoot")]
        string? rootPath = null,
        McpServer server = null!,
        CancellationToken ct = default)
    {
        return IndexStatusAsync(rootPath, server, ct);
    }

    private async Task<string> IndexStatusAsync(string? rootPath, McpServer server, CancellationToken ct)
    {
        await workspaceRootResolver.EnsureResolvedAsync(server, ct);

        string root = string.IsNullOrWhiteSpace(rootPath) ? _config.WorkspaceRoot : rootPath;
        bool running = indexTrigger.IsRunning(root);
        string? error = indexTrigger.GetLastError(root);
        object? last = null;
        if (indexTrigger.TryGetLastStats(root, out RepoIndexStats? stats) && stats is not null)
        {
            last = new
            {
                files = stats.FilesScanned,
                chunks_indexed = stats.ChunksIndexed,
                chunks_skipped = stats.ChunksSkipped,
                chunks_deleted = stats.ChunksDeleted,
                elapsed_seconds = Math.Round(stats.Elapsed.TotalSeconds, 1)
            };
        }

        return JsonSerializer.Serialize(new
        {
            root,
            state = running ? "running" : error is not null ? "failed" : last is not null ? "idle" : "not_started",
            last_completed = last,
            last_error = error
        });
    }

    [McpServerTool]
    [Description(
        "Natural-language search over the chunk index built by IndexRepo. Returns file/lines/summary/score per " +
        "hit — not raw code; read the actual file only for the 1-2 hits that look relevant. Returns an empty " +
        "result set (not an error) if IndexRepo hasn't been run yet. Ambiguous queries are silently reranked by " +
        "a second local model pass before ranking; if the best hit still scores below the confidence floor, " +
        "returns {\"match\":\"none\", best_candidate, confidence} instead — treat that as \"nothing relevant is " +
        "indexed for this,\" not as a prompt to use best_candidate anyway.")]
    public async Task<string> SemanticGrep(
        [Description("Natural-language query")] string query,
        [Description("Optional substring filter on the relative file path")]
        string? pathFilter = null,
        [Description("Number of top results to return (default 5)")]
        int topK = 5,
        McpServer server = null!,
        CancellationToken ct = default)
    {
        await workspaceRootResolver.EnsureResolvedAsync(server, ct);

        RetrievalOutcome outcome = await retrieval.RetrieveAsync(_config.WorkspaceRoot, query, pathFilter, topK, ct);

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
