using TokenSaver.AgentHelpers.Core.Models;
using TokenSaver.Config;
using TokenSaver.ContextHelper.Models;

using Microsoft.Extensions.Logging;

namespace TokenSaver.MCP.Services;

/// <summary>
///     Implements <c>repoHelper.optimizeInstructions</c> (see <see cref="RepoHelperBehaviorConfig" />):
///     when enabled, copies the machine-global Claude instructions
///     (<c>%USERPROFILE%\.claude\CLAUDE.md</c>) — via <see cref="InstructionCacheService" />'s
///     hash-gated <c>.tokensaver/context/instructions/</c> cache, which also splits out any
///     sub-docs the main doc lazy-loads on demand — into whatever workspace instruction file(s)
///     the connecting tool actually supports and reads most efficiently — see
///     <see cref="InstructionSyncResolver" /> for the per-tool target/mechanism lookup. Each
///     sub-doc syncs to its own scoped target file (translating its <c>paths</c> frontmatter into
///     the client's own glob field) so the client's native lazy-loading still applies, rather than
///     flattening everything into one always-loaded blob.
///     <para>
///     Invoked from <see cref="Middleware.AgentDetectionMiddleware" /> on every MCP
///     <c>initialize</c> handshake (i.e. every session connect), same as
///     <see cref="WorkspaceConfigDefaults.EnsureDefaults" /> — best-effort and non-blocking, an
///     I/O failure here must never break the handshake.
///     </para>
///     <para>
///     The synced content is wrapped in a delimited managed block
///     (<see cref="BlockStartMarker" />/<see cref="BlockEndMarker" />) so re-syncing never clobbers
///     content a user hand-authored elsewhere in the same file — only the block between the
///     markers is replaced (or appended, if the file/markers don't exist yet).
///     </para>
/// </summary>
public sealed partial class InstructionOptimizerService
{
    private const string BlockStartMarker =
        "<!-- tokensaver:global-instructions:start (auto-synced from %USERPROFILE%\\.claude\\CLAUDE.md — edit the source there, not here) -->";

    private const string BlockEndMarker = "<!-- tokensaver:global-instructions:end -->";

    private readonly BehaviorConfigService _behaviorConfig;
    private readonly InstructionCacheService _cache;
    private readonly ILogger<InstructionOptimizerService> _logger;

    public InstructionOptimizerService(
        BehaviorConfigService behaviorConfig, InstructionCacheService cache, ILogger<InstructionOptimizerService> logger)
    {
        _behaviorConfig = behaviorConfig;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>Env var name that overrides the source path; tests use this to avoid depending on a real user profile.</summary>
    private const string GlobalClaudeMdOverrideEnvVar = "TOKENSAVER_GLOBAL_CLAUDE_MD_PATH";

    /// <summary>The machine-global Claude instructions file TokenSaver copies from (or <c>TOKENSAVER_GLOBAL_CLAUDE_MD_PATH</c> when set).</summary>
    public static string GlobalClaudeInstructionsPath =>
        Environment.GetEnvironmentVariable(GlobalClaudeMdOverrideEnvVar) is { Length: > 0 } overridePath
            ? overridePath
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "CLAUDE.md");

    /// <summary>
    ///     Returns the cached content of a sub-doc matching <paramref name="taskType" /> (see
    ///     <see cref="InstructionCacheService.TryGetInjectableDoc" />), or <see langword="null" />
    ///     if <c>repoHelper.optimizeInstructions</c> is off for <paramref name="workspaceRoot" /> or
    ///     no sub-doc matches. Shares the same feature flag as <see cref="EnsureSynced" /> — a
    ///     workspace that has instruction optimization disabled shouldn't have content injected
    ///     into its requests either.
    /// </summary>
    public string? TryInjectRelevantDoc(string workspaceRoot, TaskType taskType)
    {
        TokenSaverBehaviorConfig config = _behaviorConfig.GetConfig(workspaceRoot);
        return config.RepoHelper?.OptimizeInstructions == false
            ? null
            : _cache.TryGetInjectableDoc(workspaceRoot, taskType);
    }

    /// <summary>
    ///     No-ops only when <c>repoHelper.optimizeInstructions</c> is explicitly <see langword="false" />
    ///     for <paramref name="workspaceRoot" /> — unset (no config.json has ever recorded a value)
    ///     means "on", matching this feature's documented on-by-default behavior; it must not
    ///     silently do nothing just because <c>WorkspaceConfigDefaults.EnsureDefaults</c> hasn't
    ///     written a config.json yet on this machine/workspace. Best-effort: every failure is
    ///     logged and swallowed.
    /// </summary>
    public void EnsureSynced(string workspaceRoot, CallerInfo caller)
    {
        try
        {
            TokenSaverBehaviorConfig config = _behaviorConfig.GetConfig(workspaceRoot);
            if (config.RepoHelper?.OptimizeInstructions == false)
            {
                return;
            }

            // Cache regeneration (and auto-split, when enabled) must run regardless of which
            // client is connecting — only the per-client target-file write below is conditional
            // on the resolved sync target. Checking target.ShouldSync before this and returning
            // early would mean a Claude Code connection (NativelyLoaded: ShouldSync=false, since
            // it already reads the real CLAUDE.md natively) never regenerates the cache at all —
            // silently starving TryGetInjectableDoc/auto-split for the most common real client.
            bool autoSplit = config.RepoHelper?.AutoSplitOversizedInstructions != false;
            InstructionManifest manifest = _cache.EnsureFresh(workspaceRoot, autoSplit);

            InstructionSyncTarget target = InstructionSyncResolver.Resolve(caller.ClientName);
            if (!target.ShouldSync || target.RelativeFilePath is null)
            {
                LogSkipped(_logger, caller.ClientName, target.Reason);
                return;
            }

            string mainCachePath = InstructionCacheService.MainDocCachePath(workspaceRoot);
            if (!File.Exists(mainCachePath))
            {
                LogNoGlobalFile(_logger, GlobalClaudeInstructionsPath);
                return;
            }

            string globalContent = File.ReadAllText(mainCachePath).Trim();
            if (globalContent.Length > 0)
            {
                SyncOne(workspaceRoot, caller.ClientName, target, globalContent);
            }

            // Sub-docs the main doc lazy-loads on demand each get their own target file so the
            // client's own lazy-loading still applies to them — see InstructionSyncResolver.ResolveSubDoc.
            foreach (InstructionManifestSubDoc sub in manifest.SubDocs)
            {
                InstructionSyncTarget? subTarget = InstructionSyncResolver.ResolveSubDoc(caller.ClientName, sub.Slug, sub.PathGlobs);
                if (subTarget is null)
                {
                    continue;
                }

                string subCachePath = Path.Combine(workspaceRoot, ".tokensaver", "context", "instructions", "subdocs", sub.Slug + ".md");
                if (!File.Exists(subCachePath))
                {
                    continue;
                }

                string subContent = File.ReadAllText(subCachePath).Trim();
                if (subContent.Length > 0)
                {
                    SyncOne(workspaceRoot, caller.ClientName, subTarget, subContent);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogSyncFailed(_logger, workspaceRoot, ex);
        }
    }

    private void SyncOne(string workspaceRoot, string clientName, InstructionSyncTarget target, string content)
    {
        string targetPath = Path.Combine(workspaceRoot, target.RelativeFilePath!);
        string? targetDir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        string block = BuildManagedBlock(content, target.FrontMatter);
        string existing = File.Exists(targetPath) ? File.ReadAllText(targetPath) : "";
        string updated = ReplaceManagedBlock(existing, block);

        if (!string.Equals(updated, existing, StringComparison.Ordinal))
        {
            File.WriteAllText(targetPath, updated);
            LogSynced(_logger, clientName, target.RelativeFilePath!, target.Reason);
        }
    }

    private static string BuildManagedBlock(string globalContent, string? frontMatter)
    {
        return BlockStartMarker + Environment.NewLine +
               (frontMatter ?? "") +
               globalContent + Environment.NewLine +
               BlockEndMarker;
    }

    /// <summary>
    ///     Replaces the text between <see cref="BlockStartMarker" />/<see cref="BlockEndMarker" />
    ///     in <paramref name="existing" /> with <paramref name="block" />, or appends
    ///     <paramref name="block" /> (with a blank-line separator) if the markers aren't present.
    /// </summary>
    internal static string ReplaceManagedBlock(string existing, string block)
    {
        int start = existing.IndexOf(BlockStartMarker, StringComparison.Ordinal);
        int end = existing.IndexOf(BlockEndMarker, StringComparison.Ordinal);

        if (start < 0 || end < 0 || end < start)
        {
            return existing.Length == 0
                ? block + Environment.NewLine
                : existing.TrimEnd() + Environment.NewLine + Environment.NewLine + block + Environment.NewLine;
        }

        string before = existing[..start];
        string after = existing[(end + BlockEndMarker.Length)..];
        return before + block + after;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Instruction sync skipped for client '{Client}': {Reason}")]
    private static partial void LogSkipped(ILogger logger, string client, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Instruction sync skipped — no global instructions file at {Path}")]
    private static partial void LogNoGlobalFile(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Synced global instructions into {RelativePath} for client '{Client}' ({Reason})")]
    private static partial void LogSynced(ILogger logger, string client, string relativePath, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Instruction sync failed for workspace {WorkspaceRoot}; continuing without it")]
    private static partial void LogSyncFailed(ILogger logger, string workspaceRoot, Exception ex);
}
