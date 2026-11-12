using System.ComponentModel;

using TokenSaver.CodeHelper.Indexing;
using TokenSaver.CodeHelper.Models.Index;
using TokenSaver.Common.Core.Configuration;
using TokenSaver.MCP.Services;

using Microsoft.Extensions.Options;

using ModelContextProtocol.Server;

namespace TokenSaver.MCP.Tools;

/// <summary>
///     Lets codegen and other out-of-band writers tell the index which files changed, instead of
///     waiting for a git event or a full scan.
/// </summary>
[McpServerToolType]
internal sealed class IndexChangeTools(
    WorkspaceIndexService index,
    IndexChangeBuffer queue,
    WorkspaceRootSessionResolver workspaceRootResolver,
    IOptions<TokenSaverConfig> config)
{
    [McpServerTool]
    [Description(
        "Tell the index which workspace files were created, changed or deleted (e.g. after code generation). " +
        "The listed files are indexed before this returns; a listed path that no longer exists is removed " +
        "from the index. Unlisted files are not read.")]
    public async Task<string> IndexFilesChanged(
        [Description("Workspace-relative paths that were created, changed or deleted")]
        string[] paths,
        [Description("Workspace root path")] string? root = null,
        McpServer server = null!,
        CancellationToken ct = default)
    {
        await workspaceRootResolver.EnsureResolvedAsync(server, ct);

        if (paths is null || paths.Length == 0)
        {
            return "No paths given.";
        }

        string r = root ?? config.Value.WorkspaceRoot;
        queue.Enqueue(r, paths);
        IndexStats? stats = await queue.FlushAsync(index, r, ct);

        return stats is null
            ? "Nothing pending."
            : $"Indexed {stats.FilesIndexed} changed file(s); {stats.FilesSkipped} unchanged; " +
              $"{stats.ReferenceFilesRead} reference file(s) re-read.";
    }
}
