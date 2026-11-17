using TokenSaver.Common.Core.Configuration;
using TokenSaver.Common.Contracts;
using TokenSaver.TokenEconomy.Models.TokenEconomy;
using TokenSaver.TokenEconomy.Contracts;
using TokenSaver.MCP.Services;

using Microsoft.Extensions.Options;

using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Text.Json;

namespace TokenSaver.MCP.Tools;

/// <summary>
///     Session context snapshot/restore, extending the existing CONTEXT.md pattern. A large
///     incoming blob is distilled before being stored as the "quick restore" — restoring a stale
///     multi-thousand-token dump would defeat the point of a concise-context tool — while the full
///     blob is kept alongside on disk for when detail is actually needed (SessionRestore's
///     full:true). See TokenSaver/docs/token-economy-tools-contract.md.
/// </summary>
[McpServerToolType]
internal sealed class SessionTools(
    IDistillationService distill,
    IOptions<TokenSaverConfig> config,
    WorkspaceRootSessionResolver workspaceRootResolver)
{
    private const int DistillThresholdChars = 4000;
    private readonly TokenSaverConfig _config = config.Value;

    [McpServerTool]
    [Description(
        "Save a session context blob to disk for later restore. Blobs over ~4000 characters are distilled " +
        "to a quick-restore summary automatically; the full original is kept alongside on disk regardless.")]
    public async Task<string> SessionSnapshot(
        [Description("Identifier for this session — used as the snapshot filename")]
        string sessionId,
        [Description("The context blob to save")] string contextBlob,
        [Description("Optional explicit output path; defaults to <WorkspaceRoot>/.tokensaver/sessions/<sessionId>.json")]
        string? outputPath = null,
        McpServer server = null!,
        CancellationToken ct = default)
    {
        await workspaceRootResolver.EnsureResolvedAsync(server, ct);

        // No working-directory fallback: without a client-supplied root or an explicit outputPath,
        // there is nowhere correct to write the snapshot.
        if (outputPath is null && string.IsNullOrWhiteSpace(_config.WorkspaceRoot))
        {
            return JsonSerializer.Serialize(new
            {
                status = "no_root",
                message = "The client did not send a workspace folder and no outputPath was given."
            });
        }

        // sessionId becomes a filename: reject path separators and other invalid characters so it
        // cannot walk out of the sessions folder.
        if (outputPath is null && (string.IsNullOrWhiteSpace(sessionId) || sessionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            return JsonSerializer.Serialize(new
            {
                status = "invalid_session_id",
                message = "sessionId must be a plain name without path separators or invalid filename characters."
            });
        }

        string dest = outputPath ?? Path.Combine(_config.WorkspaceRoot, ".tokensaver", "sessions", $"{sessionId}.json");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

        string summary;
        if (contextBlob.Length > DistillThresholdChars)
        {
            DistillResult result = await distill.DistillAsync(
                _config.WorkspaceRoot, "sessionSnapshot", DistillRole.GenericCondense, contextBlob, maxTokens: 400, ct: ct);
            summary = result.Summary;
        }
        else
        {
            summary = contextBlob;
        }

        // ChangeExtension always yields a different path; string Replace on ".json" was a no-op for an
        // outputPath without that extension, so the full blob overwrote the snapshot itself.
        string fullBlobPath = Path.ChangeExtension(dest, ".full.txt");
        await File.WriteAllTextAsync(dest, JsonSerializer.Serialize(new
        {
            session_id = sessionId,
            saved_at = DateTimeOffset.UtcNow.ToString("O"),
            summary,
            full_blob_path = fullBlobPath
        }), ct);
        await File.WriteAllTextAsync(fullBlobPath, contextBlob, ct);

        return $"Saved snapshot: {dest}";
    }

    [McpServerTool]
    [Description(
        "Restore a previously saved session snapshot. Returns the distilled summary by default; pass full:true " +
        "to read back the original untruncated blob instead.")]
    public async Task<string> SessionRestore(
        [Description("Path to the .json snapshot file written by SessionSnapshot")]
        string snapshotPath,
        [Description("Return the full original blob instead of the distilled summary")]
        bool full = false,
        CancellationToken ct = default)
    {
        string json = await File.ReadAllTextAsync(snapshotPath, ct);
        using JsonDocument doc = JsonDocument.Parse(json);

        if (!full)
        {
            return doc.RootElement.GetProperty("summary").GetString() ?? "";
        }

        string fullBlobPath = doc.RootElement.GetProperty("full_blob_path").GetString()
            ?? throw new InvalidOperationException($"Snapshot at '{snapshotPath}' has no full_blob_path.");
        return await File.ReadAllTextAsync(fullBlobPath, ct);
    }
}
