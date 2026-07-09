using TokenSaver.Library.Configuration;
using TokenSaver.Library.Contracts;
using TokenSaver.Library.Models.TokenEconomy;

using Microsoft.Extensions.Options;

using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Text.Json;

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
    ISemanticSearchService search,
    IOptions<TokenSaverConfig> config)
{
    private readonly TokenSaverConfig _config = config.Value;

    [McpServerTool]
    [Description(
        "Chunk the workspace into function/class/markdown-section pieces and summarize+embed each new or " +
        "changed one via the local model (incremental — unchanged chunks are skipped by content hash). Run this " +
        "before SemanticGrep the first time, and again after significant edits.")]
    public async Task<string> IndexRepo(
        [Description("Workspace root to index; defaults to the configured WorkspaceRoot")]
        string? rootPath = null,
        [Description("File extensions to include, e.g. ['.cs','.md']; defaults to .cs/.js/.ts/.py/.md")]
        string[]? extensions = null,
        [Description("Max concurrent Ollama calls while indexing")]
        int concurrency = 4,
        CancellationToken ct = default)
    {
        string root = string.IsNullOrWhiteSpace(rootPath) ? _config.WorkspaceRoot : rootPath;
        RepoIndexStats stats = await indexer.IndexRepoAsync(root, extensions, concurrency, ct);
        return JsonSerializer.Serialize(new
        {
            files = stats.FilesScanned,
            chunks_indexed = stats.ChunksIndexed,
            chunks_skipped = stats.ChunksSkipped,
            chunks_deleted = stats.ChunksDeleted,
            elapsed_seconds = Math.Round(stats.Elapsed.TotalSeconds, 1)
        });
    }

    [McpServerTool]
    [Description(
        "Natural-language search over the chunk index built by IndexRepo. Returns file/lines/summary/score per " +
        "hit — not raw code; read the actual file only for the 1-2 hits that look relevant. Returns an empty " +
        "result set (not an error) if IndexRepo hasn't been run yet.")]
    public async Task<string> SemanticGrep(
        [Description("Natural-language query")] string query,
        [Description("Optional substring filter on the relative file path")]
        string? pathFilter = null,
        [Description("Number of top results to return (default 5)")]
        int topK = 5,
        CancellationToken ct = default)
    {
        IReadOnlyList<SemanticSearchHit> hits = await search.SearchAsync(_config.WorkspaceRoot, query, pathFilter, topK, ct);
        return JsonSerializer.Serialize(hits.Select(h => new
        {
            file = h.RelativePath,
            lines = $"{h.StartLine}-{h.EndLine}",
            type = h.ChunkType,
            name = h.Name,
            summary = h.Summary,
            score = h.Score
        }));
    }
}
