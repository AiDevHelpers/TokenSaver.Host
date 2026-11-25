using AiDevHelpers.AgentHelpers.Core.Contracts;
using AiDevHelpers.AgentHelpers.Core.Models;
using AiDevHelpers.AgentHelpers.Core.Services;
using AiDevTools.TokenSaver.Config;

using Microsoft.AspNetCore.Http;

namespace AiDevTools.TokenSaver.MCP.Services;

/// <summary>
///     Bridges caller detection (<see cref="AiDevHelpers.AgentHelpers.Core" />) to the
///     <c>lazyContext</c> behavior-config section (<see cref="AiDevTools.TokenSaver.Config" />) — same reason
///     this lives in the Host as <see cref="AgentConfigResolver" />: the two projects don't
///     reference each other.
///     <para>
///     Decides the effective <see cref="ContextFormat.WillAskForMissingContext" /> for the calling
///     client, so <c>BuildConciseContext</c>/<c>CleanAndChat</c> and friends know whether it's
///     safe to send summaries and let the client re-fetch full content, or whether they must send
///     complete content up front because nothing omitted will ever be seen again.
///     </para>
///     <para>
///     Resolution order, first match wins:
///     1. A hardcoded <c>OpenSource</c> <c>Client</c>-kind adapter already claims this
///        <c>ClientName</c> (Cursor/Copilot/Continue) — its measured, non-configurable value
///        always wins. These three are excluded from user override entirely: Copilot and
///        Continue are provably unsafe to treat as lazy (see their adapters' doc comments), so
///        letting config override them would silently drop content a user can't get back.
///     2. An explicit per-client entry in <c>lazyContext.overrides</c> (case-insensitive on
///        <see cref="CallerInfo.ClientName" />) — this is the "implement it for agents that don't
///        have a built-in adapter" lever, e.g. <c>{"claude-code": true}</c>.
///     3. <c>lazyContext.defaultWillAskForMissingContext</c> — the workspace/global fallback for
///        any unrecognized client name, default <see langword="false" /> (safe/eager).
///     </para>
/// </summary>
internal sealed class LazyContextResolver
{
    private static readonly HashSet<string> CursorClientNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cursor", "cursor-ide",
    };

    private static readonly HashSet<string> CopilotClientNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "copilot", "github-copilot", "vscode-copilot", "github.copilot", "github copilot", "copilot-chat",
    };

    private static readonly HashSet<string> ContinueClientNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "continue", "continue.dev", "continuedev",
    };

    private readonly ICallerDetectionService _detection;
    private readonly BehaviorConfigService _behaviorConfig;

    public LazyContextResolver(ICallerDetectionService detection, BehaviorConfigService behaviorConfig)
    {
        _detection = detection;
        _behaviorConfig = behaviorConfig;
    }

    /// <summary>
    ///     Detects the calling client from <paramref name="httpContext" /> and returns the
    ///     effective lazy-context resolution for it.
    /// </summary>
    public LazyContextResolution Resolve(HttpContext? httpContext, string workspaceRoot)
    {
        CallerInfo caller = httpContext is not null ? _detection.Detect(httpContext) : new CallerInfo();
        return Resolve(caller, workspaceRoot);
    }

    /// <summary>
    ///     Same as <see cref="Resolve(HttpContext?, string)" /> but takes an already-detected
    ///     <see cref="CallerInfo" /> — for callers (like <c>BuildConciseContext</c>) that already
    ///     ran <see cref="ICallerDetectionService.Detect" /> themselves and would otherwise detect
    ///     the caller twice per request.
    /// </summary>
    public LazyContextResolution Resolve(CallerInfo caller, string workspaceRoot)
    {
        LazyContextBehaviorConfig? config = _behaviorConfig.GetConfig(workspaceRoot).LazyContext;
        (bool willAsk, LazyContextSource source) = Resolve(caller.ClientName, config);
        return new LazyContextResolution(caller, willAsk, source);
    }

    /// <summary>
    ///     Pure resolution step, split out so it can be unit tested without an
    ///     <see cref="HttpContext" />/workspace on disk.
    /// </summary>
    internal static (bool WillAskForMissingContext, LazyContextSource Source) Resolve(
        string clientName, LazyContextBehaviorConfig? config)
    {
        if (!string.IsNullOrEmpty(clientName))
        {
            if (CursorClientNames.Contains(clientName))
            {
                return (true, LazyContextSource.BuiltInAdapter);
            }

            if (CopilotClientNames.Contains(clientName) || ContinueClientNames.Contains(clientName))
            {
                return (false, LazyContextSource.BuiltInAdapter);
            }

            if (config?.Overrides is { } overrides
                && overrides.TryGetValue(clientName, out bool overridden))
            {
                return (overridden, LazyContextSource.ConfigOverride);
            }
        }

        return (config?.DefaultWillAskForMissingContext ?? false, LazyContextSource.WorkspaceDefault);
    }
}

/// <summary>Result of <see cref="LazyContextResolver.Resolve(HttpContext?, string)" />.</summary>
internal sealed record LazyContextResolution(CallerInfo Caller, bool WillAskForMissingContext, LazyContextSource Source);

/// <summary>Which resolution step actually decided <see cref="LazyContextResolution.WillAskForMissingContext" />.</summary>
internal enum LazyContextSource
{
    /// <summary>A hardcoded, non-overridable <c>OpenSource</c> client adapter (Cursor/Copilot/Continue) decided it.</summary>
    BuiltInAdapter,

    /// <summary>An explicit <c>lazyContext.overrides</c> entry for this client name decided it.</summary>
    ConfigOverride,

    /// <summary><c>lazyContext.defaultWillAskForMissingContext</c> (or its hardcoded false fallback) decided it — no adapter or override matched.</summary>
    WorkspaceDefault,
}
