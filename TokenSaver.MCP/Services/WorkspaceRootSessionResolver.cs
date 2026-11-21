using AiDevHelpers.AgentHelpers.Core.Models;
using TokenSaver.CodeHelper.Configuration;
using TokenSaver.CodeHelper.Indexing;
using TokenSaver.CodeHelper.Models.Index;
using TokenSaver.Config;
using TokenSaver.ContextHelper.Configuration;
using TokenSaver.Common.Core.Configuration;
using TokenSaver.TokenEconomy.Models.Context;
using TokenSaver.TokenEconomy.Services;
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
        Message = "No workspace root available yet (client sent no MCP roots and no rootPath was given) — indexing and context warm-up deferred.")]
    private static partial void LogWorkspaceRootUnresolved(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Unified index ready: {Files} files, {Symbols} symbols ({Skipped} unchanged) in {Elapsed:F1}s")]
    private static partial void LogUnifiedIndexReady(ILogger logger, int files, int symbols, int skipped, double elapsed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Initial unified index skipped")]
    private static partial void LogInitialIndexSkipped(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Initial semantic index start skipped")]
    private static partial void LogSemanticIndexStartSkipped(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SimpleModelServer not available after 10 min — context warm-up skipped.")]
    private static partial void LogSimpleModelServerNotAvailable(ILogger logger);

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
    private readonly SemanticIndexBackgroundTrigger _semanticIndexTrigger;
    private readonly ILogger<WorkspaceRootSessionResolver> _logger;
    private readonly WorkspaceSessionRoots _sessionRoots;

    /// <summary>How long to wait for a client to answer a roots request before giving up on it.</summary>
    internal static readonly TimeSpan RootsRequestTimeout = TimeSpan.FromSeconds(5);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _warmedRoots =
        new(StringComparer.OrdinalIgnoreCase);

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
        SemanticIndexBackgroundTrigger semanticIndexTrigger,
        WorkspaceSessionRoots sessionRoots,
        ILogger<WorkspaceRootSessionResolver> logger)
    {
        _config = config.Value;
        _contextHelperConfig = contextHelperConfig;
        _indexService = indexService;
        _contextPipeline = contextPipeline;
        _ollama = ollama;
        _behaviorConfigOverlay = behaviorConfigOverlay;
        _instructionOptimizer = instructionOptimizer;
        _semanticIndexTrigger = semanticIndexTrigger;
        _sessionRoots = sessionRoots;
        _logger = logger;
    }

    /// <summary>
    ///     No-op if a workspace root is already known. Otherwise tries the connecting client's MCP
    ///     roots capability, then falls back to a cwd repo-root walk. Never throws — a resolution
    ///     failure just leaves <see cref="TokenSaverConfig.WorkspaceRoot" /> empty, same as the
    ///     "not configured" case tools already tolerate today.
    /// </summary>
    public Task EnsureResolvedAsync(McpServer server, CancellationToken ct) =>
        EnsureResolvedAsync(server, rootHint: null, ct);

    /// <summary>
    ///     Same as <see cref="EnsureResolvedAsync(McpServer, CancellationToken)" />, plus an explicit
    ///     folder from the tool call (e.g. IndexRepo's rootPath). A hint that names an existing
    ///     directory starts that folder's warm-up and semantic index on this call, once per process,
    ///     without changing the default <see cref="TokenSaverConfig.WorkspaceRoot" />. Clients that
    ///     send no MCP roots otherwise never resolve a root here.
    /// </summary>
    public async Task EnsureResolvedAsync(McpServer server, string? rootHint, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(rootHint) && Directory.Exists(rootHint))
        {
            TriggerWarmupOnce(Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootHint)));
        }

        // This session already resolved its root (the request scope is seeded from the per-session
        // store), so skip the client round-trip and the per-root setup below.
        if (WorkspaceScope.Current is { Root: { Length: > 0 } })
        {
            return;
        }

        // Only the client's MCP roots are trusted. The server's own working directory is never used:
        // it says nothing about which folder the session is in. A tool call that carries rootPath
        // resolves through the hint overload instead.
        // A null server has no client roots to ask (in-process callers such as tests); treat it as unresolved.
        string? resolved = server is null ? null : await TryResolveViaClientRootsAsync(server, ct);

        if (resolved is null)
        {
            LogWorkspaceRootUnresolved(_logger);
            return;
        }

        // Per-session, not process-wide: this request's scope and the session store carry the root;
        // the config singleton only remembers it for callers that run outside any request.
        if (WorkspaceScope.Current is { } scope)
        {
            scope.Root = resolved;
        }

        _sessionRoots.Set(server?.SessionId, resolved);

        _config.RecordResolvedRoot(resolved);
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
        _instructionOptimizer.EnsureSynced(resolved, new CallerInfo { ClientName = server?.ClientInfo?.Name ?? "" });

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
            // A client that never opens the session's event stream cannot answer a roots request;
            // bound the wait so such a call degrades to "no root" instead of hanging.
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RootsRequestTimeout);
            ListRootsResult result = await server.RequestRootsAsync(new ListRootsRequestParams(), timeout.Token);
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
        // Once per workspace root, not once per process: a later session that connects to a
        // different folder in the same process must still get its own index and warm-up.
        if (!_warmedRoots.TryAdd(root, 0))
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

            // Folder/file index table (docs/folder-index.md), refreshed on connection like the code
            // and semantic indexes. Only for workspaces that have a docs/ folder to index.
            try
            {
                if (Directory.Exists(Path.Combine(root, "docs")))
                {
                    _ = FolderFileTable.Generate(root);
                }
            }
            catch (Exception ex)
            {
                LogInitialIndexSkipped(_logger, ex);
            }
        });

        // Chunk/embedding index for SemanticGrep. Runs on every new session root, independent of
        // AutoWarmOnStartup and of the router being ready: EnsureStartedAsync is a cheap no-op once
        // the Chunks table has rows, and otherwise starts a detached run with CancellationToken.None.
        _ = Task.Run(async () =>
        {
            try
            {
                await _semanticIndexTrigger.EnsureStartedAsync(root, CancellationToken.None);
            }
            catch (Exception ex)
            {
                LogSemanticIndexStartSkipped(_logger, ex);
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
                    LogSimpleModelServerNotAvailable(_logger);
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
