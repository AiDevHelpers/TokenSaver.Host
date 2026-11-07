using TokenSaver.AgentHelpers.Core.Models;
using TokenSaver.CodeHelper.Configuration;
using TokenSaver.CodeHelper.Indexing;
using TokenSaver.CodeHelper.Models.Index;
using TokenSaver.Config;
using TokenSaver.ContextHelper.Configuration;
using TokenSaver.Common.Core.Configuration;
using TokenSaver.TokenEconomy.Models.Context;
using TokenSaver.OllamaIntegration.Services;

using Microsoft.Extensions.Options;

using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;


namespace TokenSaver.MCP.Services;

/// <summary>
///     Resolves the workspace root lazily, on the first tool call of an MCP session, instead of
///     eagerly at process startup. Replaces the old %ProgramData%\TokenSaver\workspace-root.json
///     bootstrap cache: rather than remembering whichever repo last connected in a global file,
///     the connecting client tells this server which workspace it's serving, via the MCP protocol's
///     roots capability (<see cref="McpServer.RequestRootsAsync" />). Falls back to a cwd-based
///     repo-root walk (<see cref="WorkspaceRootResolver.FindRepoRoot" />) for clients that don't
///     declare roots support.
///     <para>
///     Once a root is resolved, indexing and (if enabled) context-pipeline warm-up are kicked off
///     exactly once for the process's lifetime — this used to run unconditionally at startup in
///     Program.cs; it now only runs once we actually know which workspace to index.
///     </para>
/// </summary>
internal sealed partial class WorkspaceRootSessionResolver
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Client roots request failed; falling back to cwd repo-root walk")]
    private static partial void LogRootsRequestFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Workspace root resolved at session connect: {Root}")]
    private static partial void LogWorkspaceRootResolved(ILogger logger, string root);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "No workspace root available yet (client has no roots and no repo markers found from cwd) — indexing and context warm-up deferred.")]
    private static partial void LogWorkspaceRootUnresolved(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Unified index ready: {Files} files, {Symbols} symbols ({Skipped} unchanged) in {Elapsed:F1}s")]
    private static partial void LogUnifiedIndexReady(ILogger logger, int files, int symbols, int skipped, double elapsed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Initial unified index skipped")]
    private static partial void LogInitialIndexSkipped(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "DraftModel not available after 10 min — context warm-up skipped.")]
    private static partial void LogDraftModelNotAvailable(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent context warmed: {Generated} generated files, {Tokens} tokens")]
    private static partial void LogAgentContextWarmed(ILogger logger, int generated, int tokens);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Context warm-up skipped")]
    private static partial void LogContextWarmupSkipped(ILogger logger, Exception ex);

    private readonly TokenSaverConfig _config;
    private readonly IOptions<ContextHelperConfig> _contextHelperConfig;
    private readonly WorkspaceIndexService _indexService;
    private readonly ContextPipelineService _contextPipeline;
    private readonly OllamaService _ollama;
    private readonly BehaviorConfigOverlay _behaviorConfigOverlay;
    private readonly InstructionOptimizerService _instructionOptimizer;
    private readonly ILogger<WorkspaceRootSessionResolver> _logger;
    private int _warmupTriggered;

    /// <summary>
    ///     Test-only escape hatch: unit tests construct tool classes (and this resolver) directly,
    ///     bypassing the real MCP session lifecycle — without this, every test that exercises a
    ///     workspace-scoped tool method would kick off a real background index/context-warmup pass
    ///     against that test's temp workspace, racing the test's own assertions. Defaults to
    ///     <see langword="false" /> (real behavior) for every production/DI-resolved instance.
    /// </summary>
    internal bool SkipBackgroundWarmupForTests { get; set; }

    public WorkspaceRootSessionResolver(
        IOptions<TokenSaverConfig> config,
        IOptions<ContextHelperConfig> contextHelperConfig,
        WorkspaceIndexService indexService,
        ContextPipelineService contextPipeline,
        OllamaService ollama,
        BehaviorConfigOverlay behaviorConfigOverlay,
        InstructionOptimizerService instructionOptimizer,
        ILogger<WorkspaceRootSessionResolver> logger)
    {
        _config = config.Value;
        _contextHelperConfig = contextHelperConfig;
        _indexService = indexService;
        _contextPipeline = contextPipeline;
        _ollama = ollama;
        _behaviorConfigOverlay = behaviorConfigOverlay;
        _instructionOptimizer = instructionOptimizer;
        _logger = logger;
    }

    /// <summary>
    ///     No-op if a workspace root is already known. Otherwise tries the connecting client's MCP
    ///     roots capability, then falls back to a cwd repo-root walk. Never throws — a resolution
    ///     failure just leaves <see cref="TokenSaverConfig.WorkspaceRoot" /> empty, same as the
    ///     "not configured" case tools already tolerate today.
    /// </summary>
    public async Task EnsureResolvedAsync(McpServer server, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_config.WorkspaceRoot))
        {
            TriggerWarmupOnce(_config.WorkspaceRoot);
            return;
        }

        string? resolved = await TryResolveViaClientRootsAsync(server, ct);
        resolved ??= WorkspaceRootResolver.FindRepoRoot(Directory.GetCurrentDirectory());

        if (resolved is null)
        {
            LogWorkspaceRootUnresolved(_logger);
            return;
        }

        _config.WorkspaceRoot = resolved;
        Environment.SetEnvironmentVariable("SIMPLEMODELSERVER_WORKSPACE", resolved);
        LogWorkspaceRootResolved(_logger, resolved);

        // Same bootstrap AgentDetectionMiddleware runs on `initialize` when the root is already
        // known then — needed here too since the root may only become known now, after that
        // handshake already ran with an empty WorkspaceRoot.
        WorkspaceConfigDefaults.EnsureDefaults(resolved, _logger);

        // Same rationale as EnsureDefaults above, for InstructionOptimizerService.EnsureSynced —
        // AgentDetectionMiddleware's own call to it (gated on WorkspaceRoot being non-empty at
        // that point) is a no-op on the common case where the root only becomes known here, on a
        // fresh process's first real session connect. Without this fallback call, instruction
        // sync would silently never run at all for that connect (only on a later one, once
        // WorkspaceRoot happens to already be populated) despite optimizeInstructions being on by
        // default. server.ClientInfo is populated by now (this runs after the initialize
        // handshake completes), so a minimal CallerInfo built directly from it is enough here —
        // no HttpContext-dependent header/user-agent signals to fall back to like
        // CallerDetectionService has, but ClientInfo.name is the primary signal anyway.
        _instructionOptimizer.EnsureSynced(resolved, new CallerInfo { ClientName = server.ClientInfo?.Name ?? "" });

        // BehaviorConfigOverlay.Apply() no-ops while WorkspaceRoot is empty (see its own doc
        // comment) — its startup call in Program.cs and any ConfigChanged-triggered call both ran
        // before this point had a chance to fire, so without this explicit call here,
        // .tokensaver/config.json overrides for CodeHelper/TokenEconomy/ContextHelper would never
        // actually apply for the (now-common) case where the workspace root isn't known until a
        // session resolves it here, rather than at process startup.
        _behaviorConfigOverlay.Apply();

        TriggerWarmupOnce(resolved);
    }

    private async Task<string?> TryResolveViaClientRootsAsync(McpServer server, CancellationToken ct)
    {
        if (server.ClientCapabilities?.Roots is null)
        {
            return null;
        }

        try
        {
            ListRootsResult result = await server.RequestRootsAsync(new ListRootsRequestParams(), ct);
            Root? first = result.Roots?.FirstOrDefault();
            if (first is not null &&
                Uri.TryCreate(first.Uri, UriKind.Absolute, out Uri? uri) &&
                uri.IsFile)
            {
                return uri.LocalPath;
            }
        }
        catch (Exception ex) when (ex is McpException or InvalidOperationException or OperationCanceledException)
        {
            LogRootsRequestFailed(_logger, ex);
        }

        return null;
    }

    /// <summary>
    ///     Fires indexing + (if enabled) context warm-up exactly once per process, the first time a
    ///     workspace root becomes known — whether that's on this call or a burst of concurrent tool
    ///     calls right after a session connects.
    /// </summary>
    private void TriggerWarmupOnce(string root)
    {
        if (Interlocked.Exchange(ref _warmupTriggered, 1) != 0)
        {
            return;
        }

        if (SkipBackgroundWarmupForTests)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                IndexStats stats = await _indexService.IndexWorkspaceAsync(root);
                LogUnifiedIndexReady(_logger, stats.FilesIndexed, stats.SymbolsIndexed, stats.FilesSkipped, stats.Elapsed.TotalSeconds);
            }
            catch (Exception ex)
            {
                LogInitialIndexSkipped(_logger, ex);
            }
        });

        if (!_contextHelperConfig.Value.AutoWarmOnStartup)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                bool ready = await _ollama.WaitForReadyAsync(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(10), _logger);
                if (!ready)
                {
                    LogDraftModelNotAvailable(_logger);
                    return;
                }

                BuildContextResult result = await _contextPipeline.BuildConciseContextAsync(
                    "[agent boot] initialize context cache",
                    sessionId: "agent-boot");
                LogAgentContextWarmed(_logger, result.GeneratedFiles.Count, result.TokenCount);
            }
            catch (Exception ex)
            {
                LogContextWarmupSkipped(_logger, ex);
            }
        });
    }
}
