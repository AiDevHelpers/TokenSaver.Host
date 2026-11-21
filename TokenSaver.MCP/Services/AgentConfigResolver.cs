using AiDevHelpers.AgentHelpers.Core.Contracts;
using AiDevHelpers.AgentHelpers.Core.Models;
using AiDevHelpers.AgentHelpers.Core.Services;
using TokenSaver.Config;

using Microsoft.AspNetCore.Http;

namespace TokenSaver.MCP.Services;

/// <summary>
///     Bridges caller detection (<see cref="AiDevHelpers.AgentHelpers.Core" />) to behavior config
///     (<see cref="TokenSaver.Config" />) — the two projects don't reference each other, so this
///     lives in the Host where both are already wired up.
///     Detects which AI model family is calling (Anthropic/DeepSeek/Qwen/OpenSource) via the same
///     <see cref="ICallerDetectionService" />/<see cref="AgentAdapterRegistry" /> pipeline the
///     context-formatting adapters already use, then resolves the effective
///     <see cref="AgentBehaviorConfig" /> for that family: the family's own section in
///     <c>agentConfig</c> (from <c>.tokensaver/config.json</c>/global config) overlaid on the
///     top-level <c>agentConfig</c> defaults, exactly like every other behavior-config section's
///     local-over-global merge — just keyed by detected family instead of by scope.
///     Stateless and cheap (no I/O beyond <see cref="BehaviorConfigService" />'s own in-memory
///     cache) — safe to call on every MCP request rather than needing a persisted session, since
///     this server has no MCP session store (see <c>ContextInterceptMiddleware</c>).
/// </summary>
internal sealed class AgentConfigResolver
{
    private readonly ICallerDetectionService _detection;
    private readonly AgentAdapterRegistry _registry;
    private readonly BehaviorConfigService _behaviorConfig;

    public AgentConfigResolver(
        ICallerDetectionService detection,
        AgentAdapterRegistry registry,
        BehaviorConfigService behaviorConfig)
    {
        _detection = detection;
        _registry = registry;
        _behaviorConfig = behaviorConfig;
    }

    /// <summary>
    ///     Detects the calling model family from <paramref name="httpContext" /> and returns both
    ///     the resolved <see cref="CallerInfo" /> and the effective agent config for that family
    ///     (family-specific overrides merged over the workspace's <c>agentConfig</c> defaults).
    ///     Falls back to workspace defaults with no family narrowing when detection can't identify
    ///     a specific model family (<see cref="DefaultAgentAdapter" /> resolved).
    /// </summary>
    /// <param name="httpContext">The current HTTP context.</param>
    /// <param name="workspaceRoot">The resolved workspace root.</param>
    /// <param name="mcpClientName">
    ///     The connecting MCP client's <c>clientInfo.name</c>, if already known (e.g. parsed from
    ///     the <c>initialize</c> request body by <c>AgentDetectionMiddleware</c> before the MCP SDK
    ///     itself has a chance to populate <c>IMcpServer.ClientInfo</c>). Pass null when not
    ///     available — detection then falls back to header-based signals only.
    /// </param>
    public AgentConfigResolution Resolve(HttpContext? httpContext, string workspaceRoot, string? mcpClientName = null)
    {
        CallerInfo caller = httpContext is not null ? _detection.Detect(httpContext, mcpClientName) : new CallerInfo();
        IAgentAdapter modelAdapter = _registry.Resolve(caller);

        AgentBehaviorConfig? agentConfig = _behaviorConfig.GetConfig(workspaceRoot).AgentConfig;
        EffectiveAgentSettings effective = Resolve(agentConfig, modelAdapter.Family);

        return new AgentConfigResolution(caller, modelAdapter.Family, effective);
    }

    /// <summary>
    ///     Pure merge step, split out from <see cref="Resolve(HttpContext?, string)" /> so it can
    ///     be unit tested without an <see cref="HttpContext" />/workspace on disk. Family lookup is
    ///     case-insensitive against <see cref="IAgentAdapter.Family" /> values ("Anthropic",
    ///     "DeepSeek", "Qwen", "OpenSource", "Default").
    /// </summary>
    internal static EffectiveAgentSettings Resolve(AgentBehaviorConfig? agentConfig, string family)
    {
        bool? enable = agentConfig?.EnablePromptCaching;
        int? ttl = agentConfig?.CacheTtlSeconds;
        int? minTokens = agentConfig?.MinCacheableTokens;

        return family switch
        {
            "Anthropic" => ResolveAnthropic(agentConfig?.Anthropic, enable, ttl, minTokens),
            "DeepSeek" => ResolveDeepSeek(agentConfig?.DeepSeek, enable, ttl, minTokens),
            "Qwen" => ResolveQwen(agentConfig?.Qwen, enable, ttl, minTokens),
            "OpenSource" => ResolveOpenSource(agentConfig?.OpenSource, enable),
            _ => new EffectiveAgentSettings
            {
                // Default/unmatched adapter (Copilot, Continue, Cursor client adapters, or no
                // model family detected at all) — expose only the global defaults; none of the
                // per-family mechanisms apply to an unknown model.
                EnablePromptCaching = enable,
                CacheTtlSeconds = ttl,
                MinCacheableTokens = minTokens,
            },
        };
    }

    private static EffectiveAgentSettings ResolveAnthropic(AnthropicAgentConfig? a, bool? enable, int? ttl, int? minTokens) => new()
    {
        EnablePromptCaching = a?.EnablePromptCaching ?? enable,
        CacheTtlSeconds = a?.CacheTtlSeconds ?? ttl,
        MinCacheableTokens = a?.MinCacheableTokens ?? minTokens,
        EnableContextEditing = a?.EnableContextEditing,
        ContextEditingTriggerTokens = a?.ContextEditingTriggerTokens,
        ContextEditingKeepToolUses = a?.ContextEditingKeepToolUses,
    };

    private static EffectiveAgentSettings ResolveDeepSeek(DeepSeekAgentConfig? d, bool? enable, int? ttl, int? minTokens) => new()
    {
        EnablePromptCaching = d?.EnablePromptCaching ?? enable,
        CacheTtlSeconds = ttl,
        MinCacheableTokens = minTokens,
    };

    private static EffectiveAgentSettings ResolveQwen(QwenAgentConfig? q, bool? enable, int? ttl, int? minTokens) => new()
    {
        EnablePromptCaching = q?.EnablePromptCaching ?? enable,
        CacheTtlSeconds = q?.CacheTtlSeconds ?? ttl,
        MinCacheableTokens = q?.MinCacheableTokens ?? minTokens,
    };

    private static EffectiveAgentSettings ResolveOpenSource(OpenSourceAgentConfig? o, bool? enable) => new()
    {
        EnablePromptCaching = o?.EnablePromptCaching ?? enable,
        UseFlashAttention = o?.UseFlashAttention,
        KvCacheQuantization = o?.KvCacheQuantization,
        NumCtx = o?.NumCtx,
    };
}

/// <summary>Result of <see cref="AgentConfigResolver.Resolve(HttpContext?, string)" />.</summary>
internal sealed record AgentConfigResolution(CallerInfo Caller, string Family, EffectiveAgentSettings Settings);

/// <summary>
///     The agent-config knobs that apply to the currently detected caller, after merging the
///     family-specific override (if any) over the workspace's <c>agentConfig</c> defaults. Fields
///     irrelevant to the resolved family are left <see langword="null" /> (e.g.
///     <see cref="KvCacheQuantization" /> stays unset for every family except OpenSource).
/// </summary>
internal sealed record EffectiveAgentSettings
{
    public bool? EnablePromptCaching { get; init; }
    public int? CacheTtlSeconds { get; init; }
    public int? MinCacheableTokens { get; init; }

    // Anthropic-only
    public bool? EnableContextEditing { get; init; }
    public int? ContextEditingTriggerTokens { get; init; }
    public int? ContextEditingKeepToolUses { get; init; }

    // OpenSource/Ollama-only
    public bool? UseFlashAttention { get; init; }
    public string? KvCacheQuantization { get; init; }
    public int? NumCtx { get; init; }
}
