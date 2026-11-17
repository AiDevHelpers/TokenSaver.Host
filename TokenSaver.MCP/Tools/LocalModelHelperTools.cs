using TokenSaver.AgentHelpers.Core.Contracts;
using TokenSaver.AgentHelpers.Core.Models;
using TokenSaver.AgentHelpers.Core.Services;

using TokenSaver.CodeHelper.Indexing;
using TokenSaver.Config;
using TokenSaver.ContextHelper.Models;
using TokenSaver.ContextHelper.Models.Diagnostics;
using TokenSaver.ContextHelper.Pipeline;
using TokenSaver.Common.Core.Configuration;
using TokenSaver.Common.Models;
using TokenSaver.TokenEconomy.Models.Context;
using TokenSaver.OllamaIntegration.Services;
using TokenSaver.MCP.Services;
using ContextBuildResult = TokenSaver.TokenEconomy.Models.Context.BuildContextResult;
using ContextHygieneRpt = TokenSaver.TokenEconomy.Models.Context.ContextHygieneReport;

using Microsoft.Extensions.Options;

using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Text;
using System.Globalization;

using TokenSaver.TokenEconomy.Contracts;
using TokenSaver.TokenEconomy.Services;
using TokenSaver.Utilities.Services;

namespace TokenSaver.MCP.Tools;

[McpServerToolType]
internal sealed class LocalModelHelperTools
{
    private readonly AiProviderConfigService _aiProviders;
    private readonly PromptAssessor _assessor;
    private readonly ContextCleanerService _cleaner;
    private readonly TokenSaverConfig _config;
    private readonly DeferredNotificationService _deferred;
    private readonly ICallerDetectionService _detection;
    private readonly OllamaEndpointRegistry _endpoints;
    private readonly IHttpContextAccessor _httpContext;
    private readonly McpContextService _mcpContext;
    private readonly OllamaService _ollama;
    private readonly AgentAdapterRegistry _registry;
    private readonly WorkspaceConfigManager _workspaceConfig;
    private readonly ContextPipelineService _contextPipeline;
    private readonly BehaviorConfigService _behaviorConfig;
    private readonly LazyContextResolver _lazyContext;
    private readonly WorkspaceRootSessionResolver _workspaceRootResolver;
    private readonly InstructionStoreService _instructionStore;

    public LocalModelHelperTools(
        OllamaService ollama,
        ContextCleanerService cleaner,
        WorkspaceConfigManager workspaceConfig,
        ContextPipelineService contextPipeline,
        AiProviderConfigService aiProviders,
        IOptions<TokenSaverConfig> config,
        ICallerDetectionService detection,
        AgentAdapterRegistry registry,
        IHttpContextAccessor httpContext,
        PromptAssessor assessor,
        McpContextService mcpContext,
        DeferredNotificationService deferred,
        OllamaEndpointRegistry endpoints,
        BehaviorConfigService behaviorConfig,
        LazyContextResolver lazyContext,
        WorkspaceRootSessionResolver workspaceRootResolver,
        InstructionStoreService? instructionStore = null)
    {
        _instructionStore = instructionStore ?? new InstructionStoreService(InstructionStoreService.DefaultRoot);
        _behaviorConfig = behaviorConfig;
        _ollama = ollama;
        _cleaner = cleaner;
        _workspaceConfig = workspaceConfig;
        _contextPipeline = contextPipeline;
        _aiProviders = aiProviders;
        _config = config.Value;
        _detection = detection;
        _registry = registry;
        _httpContext = httpContext;
        _assessor = assessor;
        _mcpContext = mcpContext;
        _deferred = deferred;
        _endpoints = endpoints;
        _lazyContext = lazyContext;
        _workspaceRootResolver = workspaceRootResolver;
    }

    // ── CleanAndChat ──────────────────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Clean incoming prompt history and return a model response from the local Ollama model. " +
        "The prompt is automatically formatted for the configured local model before sending.")]
    public async Task<string> CleanAndChat(
        [Description("The user prompt to process")]
        string prompt,
        [Description("Optional array of conversation history entries")]
        string[]? history = null,
        [Description("Optional session identifier for health tracking")]
        string? sessionId = null,
        [Description(
            "Optional Ollama endpoint name to route this call to (see ListOllamaEndpoints). Defaults to the configured selection strategy.")]
        string? endpointName = null,
        CancellationToken ct = default)
    {
        PipelineResult pipelineResult = await _cleaner.CleanContextAsync(prompt, history, sessionId, ct: ct);
        TaskType taskType = pipelineResult.Summary.TaskType;

        // Append MCP context block to the cleaned prompt sent to the local model.
        // The local model doesn't use MCP directly, so we keep this at Mention level only.
        string mcpBlock = taskType is TaskType.General or TaskType.Explain
            ? ""
            : _mcpContext.GetContextBlock(taskType);

        string cleaned = string.IsNullOrEmpty(mcpBlock)
            ? pipelineResult.ContextText
            : pipelineResult.ContextText + "\n\n" + mcpBlock;

        // OllamaService now runs the prompt through IPromptAdapter (AgentHelpers) automatically.
        string response;
        try
        {
            response = await _ollama.CallModelOnEndpointAsync(cleaned, endpointName, ct);
        }
        catch (HttpRequestException ex)
        {
            return $"Model call failed on endpoint '{endpointName}': {ex.Message}";
        }

        // Surface pipeline warnings
        StringBuilder warnings = new();
        if (pipelineResult.SpaghettiWarning is { } s)
        {
            _ = warnings.AppendLine(CultureInfo.InvariantCulture, $"\n⚠️ Spaghetti code detected in {s.FilePath}: {s.DuplicateBlockCount} near-duplicate blocks. Would you like a refactor plan?");
        }

        if (pipelineResult.OverStrippingWarning is { } w)
        {
            _ = warnings.AppendLine(CultureInfo.InvariantCulture, $"\n{w}");
        }

        // Drain any messages queued by background services (e.g. auto-registration results).
        // This is the first point with a guaranteed active client session to deliver them through.
        string pending = _deferred.DrainFormatted();

        string result = warnings.Length > 0 ? response + warnings : response;
        return string.IsNullOrEmpty(pending) ? result : pending + result;
    }

    // ── BuildConciseContext ───────────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Build a minimal workspace context summary, adapt it for the calling AI model, " +
        "and return it ready to embed in the caller's context window. " +
        "Indexes files and folder structure into .tokensaver/context.db for on-demand retrieval " +
        "via ContextDiag(action='file') and ContextDiag(action='index'). " +
        "Files and folders listed in .gitignore are excluded unless the user explicitly names them. " +
        "Bare filenames (e.g. 'food.md') are searched across the whole workspace; all copies found " +
        "are included. When gitignored paths are found, this tool returns a confirmation question " +
        "instead of building context — ask the user, then re-call with confirmed paths in includeIgnored.")]
    public async Task<string> BuildConciseContext(
        [Description("The user prompt to process")]
        string prompt,
        [Description("Optional array of conversation history entries")]
        string[]? history = null,
        [Description("Optional session identifier for tracking context across turns")]
        string? sessionId = null,
        [Description(
            "Files or folders the user explicitly named — bare filenames like 'food.md' are searched workspace-wide")]
        string[]? requestedFiles = null,
        [Description("Optional workspace root path")]
        string? workspaceRoot = null,
        [Description("Gitignored paths the user has confirmed they want included for this conversation")]
        string[]? includeIgnored = null,
        [Description("Workspace-relative paths to exclude when multiple copies of a searched file were found")]
        string[]? excludeFiles = null,
        McpServer server = null!,
        CancellationToken ct = default)
    {
        await _workspaceRootResolver.EnsureResolvedAsync(server, ct);

        string root = workspaceRoot ?? _workspaceConfig.GetWorkspaceRoot();
        GitignoreFilter gitignore = GitignoreFilter.Load(root);
        HashSet<string> confirmed = new(includeIgnored ?? [], StringComparer.OrdinalIgnoreCase);
        HashSet<string> excluded = new(excludeFiles ?? [], StringComparer.OrdinalIgnoreCase);

        List<string> resolvedFiles = []; // workspace-relative paths to pass through
        List<string> needsConfirmation = []; // git ignored paths awaiting user approval
        List<string> searchNotes = []; // informational notes about search results

        ResolveRequestedFiles(requestedFiles, root, gitignore, confirmed, excluded, resolvedFiles, needsConfirmation, searchNotes);

        // If any git ignored paths need user approval, pause and ask before building context.
        if (needsConfirmation.Count > 0)
        {
            string listed = string.Join(", ", needsConfirmation.Select(p => $"'{p}'"));
            string param = string.Join(", ", needsConfirmation.Select(p => $"\"{p}\""));
            return string.Format(CultureInfo.InvariantCulture, "The following path(s) are listed in .gitignore: {0}.\n\n", listed) +
                   string.Format(CultureInfo.InvariantCulture, "Ask the user: do you want to include {0} for this conversation?\n\n", needsConfirmation.Count == 1 ? "it" : "them") +
                   string.Format(CultureInfo.InvariantCulture, "If yes, re-call this tool with `includeIgnored: [{0}]` added to the request.", param);
        }

        var (caller, modelAdapter, clientAdapter, format) = ResolveCallerContext(root);
        var pipeline = await BuildPipelineAsync(prompt, history, sessionId, resolvedFiles.ToArray(), root, format, ct);
        if (pipeline.RebuildJson is not null)
        {
            return pipeline.RebuildJson;
        }

        var result = pipeline.Result!;

        // Hard limit exceeded — ContextPipelineService already replaced ContextText
        // with the "CONTEXT STOPPED" message; return it as-is.
        if (result.Alert)
        {
            return result.ContextText;
        }

        // Model adapter handles context rendering; client adapter only contributes flags.
        string adapted = modelAdapter.AdaptContext(result.ContextText, caller, format);

        // If a client adapter was detected (not default), prepend its orientation header.
        if (clientAdapter.Kind == AdapterKind.Client)
        {
            string clientHeader = clientAdapter.AdaptContext("", caller, format);
            if (!string.IsNullOrWhiteSpace(clientHeader))
            {
                adapted = clientHeader + adapted;
            }
        }

        // Compact metadata header so the caller knows what was built.
        string meta = $"<!-- context: {result.GeneratedFiles.Count} files indexed, " +
                   $"~{result.TokenCount} tokens, " +
                   $"model: {modelAdapter.Family}, client: {clientAdapter.Family} -->";

        string prefix = searchNotes.Count > 0
            ? string.Join("\n", searchNotes.Select(n => $"<!-- search: {n} -->")) + "\n\n"
            : "";

        // Classify the prompt to determine MCP context verbosity for the calling AI.
        PromptAssessment assessment = await _assessor.AssessAsync(prompt, history, ct);
        string mcpBlock = _mcpContext.GetContextBlock(assessment.TaskType);

        string response = prefix + meta + "\n\n" + adapted;

        // Relevant instruction sections go into the response itself: the client only sees what this
        // tool returns, so injecting into the prompt (as the intercept middleware does) never reaches it.
        if (InstructionStoreService.InjectionEnabled)
        {
            string instructions = _instructionStore.SelectRelevantSections(prompt);
            if (instructions.Length > 0)
            {
                response += "\n\n" + instructions;
            }
        }

        // When the agent won't ask for missing content, advise the user about the
        // preferFullFileContent preference if they haven't already set it.
        if (!format.WillAskForMissingContext)
        {
            string clientFamily = clientAdapter.Family;
            WorkspacePreferences prefs = await _workspaceConfig.LoadPreferencesAsync(root);
            AgentPreferences? agentPrefs = prefs.Agents.GetValueOrDefault(clientFamily);
            if (agentPrefs?.PreferFullFileContent == null)
            {
                response +=
                    $"\n\n<!-- ℹ {clientFamily} will not auto-fetch missing file content. " +
                    $"If summaries are insufficient, ask me to run SetAgentPreference with " +
                    $"preferFullFileContent=true for {clientFamily}. -->";
            }
        }

        if (!string.IsNullOrEmpty(mcpBlock))
        {
            response += "\n\n" + mcpBlock;
        }

        response = AppendNotices(response, result, root);
        // Drain any messages queued by background services before returning to the active client.
        string pending = _deferred.DrainFormatted();
        return string.IsNullOrEmpty(pending) ? response : pending + response;
    }

    // ── SetAgentPreference ────────────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Save a per-agent workspace preference to .tokensaver/preferences.json. " +
        "Use this to tell TokenSaver how to handle context for agents that won't ask for " +
        "missing content (e.g. Copilot, Continue). " +
        "agentFamily: 'Copilot' | 'Continue' | 'Cursor' | 'Anthropic' | 'Qwen' | etc.")]
    public async Task<string> SetAgentPreference(
        [Description("Agent family name, e.g. 'Copilot', 'Continue', 'Cursor'")]
        string agentFamily,
        [Description("When true, send full file content instead of summaries (recommended for agents that won't ask)")]
        bool? preferFullFileContent = null,
        [Description("Override the per-file token limit (0 = use adapter default)")]
        int? maxFileSizeTokens = null,
        [Description("Split large files at logical boundaries instead of truncating")]
        bool? splitLargeFiles = null,
        [Description("Optional workspace root path")]
        string? workspaceRoot = null,
        McpServer server = null!,
        CancellationToken ct = default)
    {
        await _workspaceRootResolver.EnsureResolvedAsync(server, ct);

        string root = workspaceRoot ?? _workspaceConfig.GetWorkspaceRoot();
        WorkspacePreferences prefs = await _workspaceConfig.LoadPreferencesAsync(root);

        if (!prefs.Agents.TryGetValue(agentFamily, out AgentPreferences? agentPrefs))
        {
            agentPrefs = new AgentPreferences();
        }

        if (preferFullFileContent.HasValue)
        {
            agentPrefs.PreferFullFileContent = preferFullFileContent;
        }

        if (maxFileSizeTokens.HasValue)
        {
            agentPrefs.MaxFileSizeTokens = maxFileSizeTokens;
        }

        if (splitLargeFiles.HasValue)
        {
            agentPrefs.SplitLargeFiles = splitLargeFiles;
        }

        prefs.Agents[agentFamily] = agentPrefs;
        await _workspaceConfig.SavePreferencesAsync(prefs, root);

        StringBuilder lines = new();
        _ = lines.AppendLine(CultureInfo.InvariantCulture, $"Saved preferences for {agentFamily}:");
        if (agentPrefs.PreferFullFileContent.HasValue)
        {
            _ = lines.AppendLine(CultureInfo.InvariantCulture, $"  preferFullFileContent = {agentPrefs.PreferFullFileContent}");
        }

        if (agentPrefs.MaxFileSizeTokens.HasValue)
        {
            _ = lines.AppendLine(CultureInfo.InvariantCulture, $"  maxFileSizeTokens     = {agentPrefs.MaxFileSizeTokens}");
        }

        if (agentPrefs.SplitLargeFiles.HasValue)
        {
            _ = lines.AppendLine(CultureInfo.InvariantCulture, $"  splitLargeFiles       = {agentPrefs.SplitLargeFiles}");
        }

        _ = lines.Append(CultureInfo.InvariantCulture, $"Stored at: {Path.Combine(_workspaceConfig.GetLocalMcpRoot(root), "preferences.json")}");
        return lines.ToString();
    }

    // ── ContextHygiene ────────────────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Inspect the local .tokensaver context cache and return a hygiene report " +
        "for pruning and reusing cached context.")]
    public async Task<string> ContextHygiene(
        [Description("Optional session identifier")]
        string? sessionId = null,
        [Description("Optional workspace root path")]
        string? workspaceRoot = null,
        McpServer server = null!,
        CancellationToken ct = default)
    {
        await _workspaceRootResolver.EnsureResolvedAsync(server, ct);

        ContextHygieneRpt report = await _contextPipeline.BuildContextHygieneReportAsync(sessionId, workspaceRoot, ct);

        StringBuilder sb = new();
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Context cache: {report.FileCount} files, {report.SectionCount} sections.");

        if (report.StaleSections.Count > 0)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Stale sections (not accessed in 30+ days): {report.StaleSections.Count}");
            foreach (StaleSection? s in report.StaleSections.Take(5))
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  - {s.RelativePath} / {s.SectionName ?? "default"}");
            }

            if (report.StaleSections.Count > 5)
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  … and {report.StaleSections.Count - 5} more.");
            }
        }
        else
        {
            _ = sb.AppendLine("No stale sections found.");
        }

        return sb.ToString().TrimEnd();
    }

    // ── RegisterMcpProviders ──────────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Add this MCP server to the config of every installed AI client (VS Code Copilot, Claude Code, Cursor, " +
        "Continue, Cline, Sixth). Only installed clients are touched; each file is backed up first, replaced " +
        "atomically and read back, and a file that cannot be rewritten faithfully (comments, invalid JSON) is " +
        "left alone and reported with a reason. The endpoint token is written into each entry. Restart the " +
        "client afterwards. Claude Desktop is not supported (add a custom connector). See " +
        "docs/Host-MCP/provider-config-reliability.md.")]
    public async Task<string> RegisterMcpProviders(
        [Description("Override the MCP server port. Defaults to the configured server port.")]
        int? port = null,
        CancellationToken ct = default)
    {
        int effectivePort = port ?? _config.ServerPort;
        IReadOnlyList<ConfigUpdateResult> results = await _aiProviders.UpdateAllProvidersAsync(effectivePort, ct: ct);

        if (results.Count == 0)
        {
            return "No supported AI client is installed on this machine, so nothing was written. " +
                   "Use the \"TokenSaver: Open Install Prompt\" VS Code command (or INSTALL_PROMPT.md) to configure one by hand.";
        }

        StringBuilder sb = new();
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"MCP server registered at http://localhost:{effectivePort}/mcp");
        _ = sb.AppendLine();

        foreach (ConfigUpdateResult r in results)
        {
            string status = r.Success
                ? r.WasAlreadyConfigured ? "✓ already configured" : "✓ updated"
                : $"✗ failed: {r.Message}";
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  {r.Provider,-18} {status}");
            if (r.ConfigPath != null && r.Success)
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"    → {r.ConfigPath}");
            }
        }

        int updated = results.Count(r => r.Success && !r.WasAlreadyConfigured);
        int already = results.Count(r => r.Success && r.WasAlreadyConfigured);
        int failed = results.Count(r => !r.Success);
        _ = sb.AppendLine();
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Summary: {updated} updated, {already} already configured, {failed} failed.");

        if (updated > 0)
        {
            _ = sb.AppendLine("Restart affected AI clients to pick up the new MCP server entry.");
        }

        return sb.ToString().TrimEnd();
    }

    // ── ListOllamaEndpoints ───────────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "List all configured Ollama endpoints with their URL, primary flag, models, health " +
        "status, last-used timestamp, and success/failure counts. When no TokenSaver:Endpoints " +
        "are configured, returns the single default endpoint built from OllamaUrl/OllamaModel.")]
    public string ListOllamaEndpoints()
    {
        IReadOnlyList<OllamaEndpointStatus> endpoints = _endpoints.GetAllEndpoints();
        if (endpoints.Count == 0)
        {
            return "No Ollama endpoints configured.";
        }

        StringBuilder sb = new();
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"{endpoints.Count} Ollama endpoint(s) configured:");
        _ = sb.AppendLine();

        foreach (OllamaEndpointStatus e in endpoints)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  {e.Name}{(e.Primary ? " (primary)" : "")}");
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"    url:          {e.Url}");
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"    health:       {(e.Healthy ? "healthy" : "unhealthy")}");
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"    models:       {(e.Models.Count > 0 ? string.Join(", ", e.Models) : "(any)")}");
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"    lastUsed:     {(e.LastUsedAt is { } lu ? lu.ToString("u") : "never")}");
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"    lastChecked:  {(e.LastHealthCheckAt is { } lc ? lc.ToString("u") : "never")}");
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"    success/fail: {e.SuccessCount}/{e.FailureCount}");
            if (e.NextRetryAt is { } retryAt && retryAt > DateTimeOffset.UtcNow)
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"    nextRetry:    {retryAt:u}");
            }

            _ = sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }
    /// <summary>Runs the context pipeline; a broken stored schema comes back as a rebuild prompt instead of an error.</summary>
    private async Task<(ContextBuildResult? Result, string? RebuildJson)> BuildPipelineAsync(
        string prompt, string[]? history, string? sessionId, string[] files, string root, ContextFormat format, CancellationToken ct)
    {
        try
        {
            ContextBuildResult result = await _contextPipeline.BuildConciseContextAsync(
                prompt, history, sessionId, files, root,
                avoidChainOfThought: format.AvoidChainOfThought,
                perFileTokenLimit: format.PerFileTokenLimit,
                ct: ct);
            return (result, null);
        }
        catch (Exception ex) when (IndexSchemaGuard.IsSchemaMismatch(ex))
        {
            return (null, IndexSchemaGuard.NeedsRebuildJson(root, ex));
        }
    }

    /// <summary>Resolves explicitly requested files: direct paths, bare filename searches, and gitignore confirmation.</summary>
    private static void ResolveRequestedFiles(
        string[]? requestedFiles,
        string root,
        GitignoreFilter gitignore,
        HashSet<string> confirmed,
        HashSet<string> excluded,
        List<string> resolvedFiles,
        List<string> needsConfirmation,
        List<string> searchNotes)
    {


        foreach (string raw in requestedFiles ?? [])
        {
            string entry = raw.Trim().TrimEnd('/', '\\');
            if (string.IsNullOrEmpty(entry))
            {
                continue;
            }

            string abs = Path.GetFullPath(Path.Combine(root, entry));
            bool isBare = !entry.Contains('/', StringComparison.Ordinal) && !entry.Contains('\\', StringComparison.Ordinal);

            if (File.Exists(abs) || Directory.Exists(abs))
            {
                // Direct path resolution
                bool isDir = Directory.Exists(abs);
                if (!confirmed.Contains(entry) && gitignore.IsIgnored(entry, isDir))
                {
                    needsConfirmation.Add(entry);
                }
                else if (!excluded.Contains(entry))
                {
                    resolvedFiles.Add(entry);
                }
            }
            else if (isBare)
            {
                // Bare filename — search the entire workspace
                string[] found = FolderTreeManager
                    .SearchFilesByName(root, entry)
                    .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                    .ToArray();

                if (found.Length == 0)
                {
                    searchNotes.Add($"'{entry}' was not found anywhere in the workspace.");
                    continue;
                }

                string[] ignored = found.Where(r => !confirmed.Contains(r) && gitignore.IsIgnored(r, false)).ToArray();
                string[] notIgnored = found.Where(r => confirmed.Contains(r) || !gitignore.IsIgnored(r, false)).ToArray();

                needsConfirmation.AddRange(ignored);

                string[] toInclude = notIgnored.Where(r => !excluded.Contains(r)).ToArray();
                resolvedFiles.AddRange(toInclude);

                if (toInclude.Length > 1)
                {
                    searchNotes.Add(
                        string.Format(CultureInfo.InvariantCulture, "'{0}' found at {1} locations: {2}. All included. Re-call with excludeFiles to narrow down.", entry, toInclude.Length, string.Join(", ", toInclude)));
                }
                else if (toInclude.Length == 1 && found.Length > 1)
                {
                    searchNotes.Add(
                        string.Format(CultureInfo.InvariantCulture, "'{0}' found at {1} locations; {2} gitignored. Including: {3}.", entry, found.Length, ignored.Length, toInclude[0]));
                }
            }
            else
            {
                searchNotes.Add(string.Format(CultureInfo.InvariantCulture, "'{0}' was not found.", entry));
            }
        }
    }

    /// <summary>Appends pipeline, index, calibration and gitignore notices to the response.</summary>
    private string AppendNotices(string response, ContextBuildResult result, string root)
    {
        // Append pipeline narrative as a hidden comment
        if (result.PipelineSummary?.NarrativeSummary is { } narrative)
        {
            response += $"\n\n<!-- pipeline: {narrative.Replace("\n", " | ", StringComparison.Ordinal)} -->";
        }

        // First-run notice: SemanticGrep is otherwise silently empty until someone remembers to
        // call IndexRepo manually — tell the caller a background build just started instead.
        if (result.SemanticIndexStatus == SemanticIndexTriggerStatus.Started)
        {
            response +=
                "\n\n<!-- ℹ Semantic index (SemanticGrep) has never been built for this workspace — " +
                "started building it in the background now. This does not block context generation; " +
                "SemanticGrep results will be available once it finishes (a few minutes for a typical repo). -->";
        }

        // Warn about stale cached files
        if (result.StaleFiles.Count > 0)
        {
            string staleList = string.Join(", ", result.StaleFiles.Take(5));
            string more = result.StaleFiles.Count > 5 ? $" (+{result.StaleFiles.Count - 5} more)" : "";
            response +=
                $"\n\n<!-- ⚠ stale-cache: {result.StaleFiles.Count} file(s) changed since last index: {staleList}{more} — re-run BuildConciseContext to refresh -->";
        }

        // Folder/file index drift (only for workspaces that have a docs/ folder).
        string? folderIndexLine = FolderFileTable.StatusLine(root);
        if (folderIndexLine is not null && folderIndexLine != "folder-index: in sync")
        {
            response += $"\n\n<!-- ⚠ {folderIndexLine} -->";
        }

        // Retrieval self-tuning: a file requested this turn scored below the relevance bar the
        // filter applied last turn — the narrow-filter threshold is being nudged wider.
        if (result.CalibrationCheck is { AnyFailure: true } check)
        {
            IEnumerable<string> misses = check.Outcomes.Where(o => o.WasFailure)
                .Select(o => $"'{o.LateFile}' scored {o.LateFileRelevance:F2} vs the {o.PreviousMinKeptRelevance:F2} bar applied last turn");
            response +=
                $"\n\n<!-- ⚠ retrieval-calibration: {string.Join("; ", misses)}" +
                (check.ThresholdAdjusted
                    ? $" — semantic threshold adjusted (now {check.NewThresholdAdjustment:+0.00;-0.00} from baseline) -->"
                    : " -->");
        }

        // Surface warnings from the last pipeline run (stored on the cleaner)
        PipelineSummary? lastSummary = _cleaner.LastSummary;
        // Spaghetti: check manifest for any sources with many dropped blocks
        IGrouping<string, ManifestEntry>? spaghettiEntries = lastSummary?.Manifest.Entries
            .GroupBy(e => e.Source)
            .FirstOrDefault(g => g.Count(e => e.WasDropped) >= 5);
        if (spaghettiEntries != null)
        {
            response +=
                $"\n\n⚠️ Spaghetti code detected in {spaghettiEntries.Key}: {spaghettiEntries.Count(e => e.WasDropped)} near-duplicate blocks found. Would you like a plan to refactor it?";
        }

        // Ask (once) whether .tokensaver/ should be gitignored, if the user hasn't answered yet.
        if (GitignorePrompt.ShouldAsk(_behaviorConfig, root))
        {
            response += $"\n\n<!-- ℹ gitignore: {GitignorePrompt.PromptText} -->";
        }

        return response;
    }

    /// <summary>Detects the calling client and model adapters and resolves the response format.</summary>
    private CallerContext ResolveCallerContext(string root)
    {
        // Detect caller early so format can be forwarded into the pipeline
        // (needed for AvoidChainOfThought and PerFileTokenLimit).
        HttpContext? ctx = _httpContext.HttpContext;
        CallerInfo caller = ctx != null
            ? _detection.Detect(ctx)
            : new CallerInfo { Source = DetectionSource.Default };

        // Two-lane resolution: model adapter controls formatting; client adapter controls
        // behavioural flags (WillAskForMissingContext, PerFileTokenLimit).
        (IAgentAdapter? modelAdapter, IAgentAdapter? clientAdapter) = _registry.ResolveBoth(caller);
        ContextFormat format = modelAdapter.GetFormat(caller)
            .WithClientOverrides(clientAdapter.GetFormat(caller));

        // No hardcoded OpenSource client adapter (Cursor/Copilot/Continue) claimed this caller —
        // clientAdapter fell through to DefaultAgentAdapter, whose WillAskForMissingContext=true
        // is just ContextFormat.Default's C# property default, not a measured or configurable
        // decision. For that case only, let lazyContext config (LazyContextResolver) decide
        // instead — this is what makes "treat my custom/unrecognized MCP client as lazy-safe"
        // configurable instead of requiring a new hardcoded IAgentAdapter. Clients already
        // claimed by a real client adapter are untouched: LazyContextResolver's own resolution
        // order already defers to Cursor/Copilot/Continue's hardcoded value first, so this branch
        // and that resolver always agree for those three — but we skip calling it at all here to
        // make the "adapter's decision is untouched" guarantee obvious at the call site, not just
        // inside the resolver.
        if (clientAdapter.Kind != AdapterKind.Client)
        {
            LazyContextResolution lazyResolution = _lazyContext.Resolve(caller, root);
            format = format with { WillAskForMissingContext = lazyResolution.WillAskForMissingContext };
        }

        return new CallerContext(caller, modelAdapter!, clientAdapter!, format);
    }

    private sealed record CallerContext(CallerInfo Caller, IAgentAdapter ModelAdapter, IAgentAdapter ClientAdapter, ContextFormat Format);

}
