using AiDevTools.TokenSaver.CodeHelper.Indexing;
using AiDevTools.TokenSaver.Config;
using AiDevTools.TokenSaver.Config.Contracts;
using AiDevTools.TokenSaver.OllamaIntegration.Services; using AiDevTools.TokenSaver.Common.Models;
using AiDevTools.TokenSaver.MCP.Services;
using AiDevTools.TokenSaver.Utilities.Services;

using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Text.Json;

namespace AiDevTools.TokenSaver.MCP.Tools;

[McpServerToolType]
internal sealed class ManageConfigTools(
    BehaviorConfigService config,
    WorkspaceConfigManager workspaceConfig,
    AgentConfigResolver agentConfigResolver,
    LazyContextResolver lazyContextResolver,
    IHttpContextAccessor httpContext,
    WorkspaceRootSessionResolver workspaceRootResolver)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    [McpServerTool]
    [Description(
        "Read or update TokenSaver behavior configuration. Config lives in two files with the same " +
        "shape: .tokensaver/config.json (local, per-workspace) and %ProgramData%\\TokenSaver\\config.json " +
        "(global, machine-wide). Local values always win over global. Both are live-reloaded on change. " +
        "action determines what happens:\n" +
        "  get                 — show the merged config plus the local and global config file paths.\n" +
        "  set-gitignore-pref  — record whether .tokensaver/ should be in .gitignore. Requires 'add' " +
        "(true/false) and 'scope' ('local' or 'global'). When add=true, also appends .tokensaver/ to " +
        ".gitignore if not already covered.\n" +
        "  ignore-additional   — append gitignore-syntax patterns (paths[]) to .tokensaver/.ignore-additional: " +
        "extra paths to EXCLUDE from indexing/context that are not in .gitignore.\n" +
        "  ignore-bypass       — append gitignore-syntax patterns (paths[]) to .tokensaver/.ignore-bypass: " +
        "paths that ARE gitignored but should still be INDEXED/included. Bypass overrides every other ignore.\n" +
        "  set                 — set one behavior-config field. Requires 'field', 'value', and 'scope'. " +
        "Applied live (no restart needed) via BehaviorConfigOverlay for CodeHelper/TokenEconomy fields. " +
        "'field' is the short leaf name (case-insensitive), not a dotted path — the section it lives " +
        "under (codeHelper.ownership/planner/calibration, tokenEconomy) is just how it's grouped on disk. " +
        "Boolean fields: autoEnabled, requireAnchorMatch (codeHelper); enableDistill, allowTerse " +
        "(hard kill switch — false means no caller can turn terse on, not even per-call), terseByDefault " +
        "(the server-wide default when a call doesn't pass its own terse: argument), enableRawRetention, " +
        "enableSavingsTracking, enableSummaryEmbedding (tokenEconomy); autoWarmOnStartup (context — " +
        "whether the server auto-builds its concise-context cache on startup; on by default); " +
        "autoIntercept (context — whether ContextInterceptMiddleware auto-cleans every " +
        "prompt-bearing MCP tool call before forwarding it; on by default, independent of " +
        "codeHelper.autoEnabled which gates that same middleware's draft-model step). " +
        "Numeric fields: canDoThreshold, deferThreshold (codeHelper.ownership — claim vs. defer a step); " +
        "confidenceThreshold, modelCallThreshold (codeHelper.planner — hand off to the AI vs. consult " +
        "Ollama while planning); wasteThreshold, learningRate (codeHelper.calibration — how outcomes " +
        "adjust ownership weights over time); contextSizeLimit, tokenBudgetMargin, semanticThreshold " +
        "(context — token cap, budget margin, and relevance-filter cutoff for built context). " +
        "Pass value=null to clear the override and revert to the default.\n" +
        "  set-agent           — set one agentConfig field. Requires 'field', 'value', 'scope', and " +
        "optionally 'family' (omit/'global' for the top-level agentConfig default that every family " +
        "falls back to; 'anthropic'/'deepSeek'/'qwen'/'openSource' to set that family's override, " +
        "which wins over the global default for callers detected as that family — see 'get' → " +
        "detectedAgent for what's currently detected on this connection). Common fields (settable at " +
        "global or any family level except deepSeek's ttl/minTokens, which it ignores — DeepSeek's " +
        "context caching isn't configurable): enablePromptCaching (bool), cacheTtlSeconds (int — " +
        "provider-native prompt/context cache TTL: Anthropic accepts 300 or 3600; Qwen is fixed ~300 " +
        "and this is advisory), minCacheableTokens (int — floor below which a provider skips caching " +
        "silently). Anthropic-only: enableContextEditing (bool — server-side auto-clearing of old tool " +
        "results via clear_tool_uses_20250919), contextEditingTriggerTokens (int — default 100000), " +
        "contextEditingKeepToolUses (int — how many recent tool-use pairs survive a clear). " +
        "OpenSource-only (Ollama-hosted Llama/Mistral/Gemma): useFlashAttention (bool — " +
        "OLLAMA_FLASH_ATTENTION), kvCacheQuantization (string: 'f16'/'q8_0'/'q4_0'), numCtx (int — " +
        "context window size / OLLAMA_CONTEXT_LENGTH). Pass value=null to clear the override.\n" +
        "  set-lazy-context    — control whether a calling client is trusted to re-ask for content " +
        "TokenSaver didn't send up front (summaries/partial files are only safe to send when the " +
        "client will re-fetch full content on demand). NOT the same as MCP tool-schema deferral — " +
        "this only controls the shape of one MCP response's context, never which tools are visible. " +
        "Requires 'value' (bool) and 'scope'. Two forms: omit 'clientName' to set the workspace-wide " +
        "default applied to any client with no built-in adapter and no override " +
        "(defaultWillAskForMissingContext, default false = eager/complete); pass 'clientName' " +
        "(matches CallerInfo.ClientName from the MCP initialize handshake, e.g. 'claude-code', " +
        "case-insensitive) to set that one client's override. Cursor/Copilot/Continue client names " +
        "are rejected — those three already have a hardcoded, measured value from their own " +
        "adapters (Cursor=true; Copilot/Continue=false, since both provably never re-ask) and can't " +
        "be overridden here; see 'get' → detectedLazyContext for what's currently applied to this " +
        "connection. Pass value=null to clear a clientName override (workspace default still " +
        "applies); the workspace default itself cannot be cleared to null, only set true/false.")]
    public async Task<string> ManageConfig(
        [Description("Action: 'get', 'set-gitignore-pref', 'ignore-additional', 'ignore-bypass', 'set', 'set-agent', or 'set-lazy-context'")]
        string action,
        [Description("For 'set-gitignore-pref': true to add .tokensaver/ to .gitignore, false to decline")]
        bool? add = null,
        [Description("For 'set-gitignore-pref'/'set'/'set-agent': 'local' (this workspace) or 'global' (all workspaces on this machine)")]
        string? scope = null,
        [Description("For 'ignore-additional'/'ignore-bypass': gitignore-syntax patterns to append")]
        string[]? paths = null,
        [Description("Workspace root override; defaults to the configured workspace root")]
        string? root = null,
        [Description("For 'set'/'set-agent': the behavior-config field name, e.g. 'autoEnabled' or 'enablePromptCaching'")]
        string? field = null,
        [Description("For 'set'/'set-agent': the new value as a string (parsed per field type), or omit/'null' to clear the override")]
        string? value = null,
        [Description("For 'set-agent': which agentConfig section — omit/'global' for the top-level default, or 'anthropic'/'deepSeek'/'qwen'/'openSource' for that family's override")]
        string? family = null,
        [Description("For 'set-lazy-context': the client name to set an override for (matches CallerInfo.ClientName, e.g. 'claude-code'); omit to set the workspace-wide default instead")]
        string? clientName = null,
        McpServer server = null!,
        CancellationToken ct = default)
    {
        await workspaceRootResolver.EnsureResolvedAsync(server, ct);

        string workspaceRoot = root ?? workspaceConfig.GetWorkspaceRoot();

        return action switch
        {
            "get" => GetConfig(workspaceRoot),
            "set-gitignore-pref" => SetGitignorePref(workspaceRoot, add, scope),
            "ignore-additional" => AppendIgnore(WorkspaceIgnoreFilter.AdditionalPath(workspaceRoot), paths, "additional exclude"),
            "ignore-bypass" => AppendIgnore(WorkspaceIgnoreFilter.BypassPath(workspaceRoot), paths, "bypass (force-include)"),
            "set-agent" => SetAgentField(workspaceRoot, family, field, value, scope),
            "set-lazy-context" => SetLazyContext(workspaceRoot, clientName, value, scope),
            "set" => SetField(workspaceRoot, field, value, scope),
            _ => $"Unknown action '{action}'. Use: get, set-gitignore-pref, ignore-additional, ignore-bypass, set, set-agent, set-lazy-context.",
        };
    }

    private static readonly HashSet<string> NonOverridableClientNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Mirrors LazyContextResolver's CursorClientNames/CopilotClientNames/ContinueClientNames —
        // these three already have a hardcoded, measured WillAskForMissingContext value from their
        // own OpenSource client adapters, so a config override here would either be redundant
        // (Cursor) or silently unsafe (Copilot/Continue provably never re-ask for dropped content).
        "cursor", "cursor-ide",
        "copilot", "github-copilot", "vscode-copilot", "github.copilot", "github copilot", "copilot-chat",
        "continue", "continue.dev", "continuedev",
    };

    private string SetLazyContext(string workspaceRoot, string? clientName, string? value, string? scope)
    {
        if (!TryParseScope(scope, out AiDevTools.TokenSaver.Config.Contracts.ConfigScope configScope))
        {
            return "set-lazy-context requires 'scope' to be 'local' or 'global'.";
        }

        bool clear = value is null || value.Equals("null", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(clientName))
        {
            // Workspace-wide default — no "clear" concept; it always resolves to a bool
            // (LazyContextResolver falls back to false when even this is unset).
            if (clear || !bool.TryParse(value, out bool defaultValue))
            {
                return "set-lazy-context without 'clientName' requires 'value' to be true or false " +
                       "(the workspace default cannot be cleared to null — only overridden true/false).";
            }

            config.Update(workspaceRoot, configScope, current =>
            {
                LazyContextBehaviorConfig lazy = current.LazyContext ?? new LazyContextBehaviorConfig();
                return current with { LazyContext = lazy with { DefaultWillAskForMissingContext = defaultValue } };
            });
            return $"Set lazyContext default (willAskForMissingContext) = {defaultValue} ({configScope}).";
        }

        if (NonOverridableClientNames.Contains(clientName))
        {
            return $"'{clientName}' already has a hardcoded, measured lazy-context value from its " +
                   "built-in client adapter (Cursor/Copilot/Continue) and cannot be overridden.";
        }

        if (!clear && !bool.TryParse(value, out _))
        {
            return "set-lazy-context requires 'value' to be true, false, or 'null' to clear the override.";
        }

        config.Update(workspaceRoot, configScope, current =>
        {
            LazyContextBehaviorConfig lazy = current.LazyContext ?? new LazyContextBehaviorConfig();
            Dictionary<string, bool> overrides = lazy.Overrides is { } existing
                ? new Dictionary<string, bool>(existing, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            if (clear)
            {
                overrides.Remove(clientName);
            }
            else
            {
                overrides[clientName] = bool.Parse(value!);
            }

            return current with { LazyContext = lazy with { Overrides = overrides } };
        });

        return clear
            ? $"Cleared lazy-context override for '{clientName}' ({configScope}) — workspace default applies again."
            : $"Set lazy-context override for '{clientName}' = {value} ({configScope}).";
    }

    private static readonly HashSet<string> AgentCommonBoolFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "enablePromptCaching",
    };

    private static readonly HashSet<string> AgentCommonIntFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "cacheTtlSeconds", "minCacheableTokens",
    };

    private static readonly HashSet<string> AnthropicOnlyBoolFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "enableContextEditing",
    };

    private static readonly HashSet<string> AnthropicOnlyIntFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "contextEditingTriggerTokens", "contextEditingKeepToolUses",
    };

    private static readonly HashSet<string> OpenSourceOnlyBoolFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "useFlashAttention",
    };

    private static readonly HashSet<string> OpenSourceOnlyIntFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "numCtx",
    };

    private string SetKvCacheQuantization(string workspaceRoot, AiDevTools.TokenSaver.Config.Contracts.ConfigScope configScope, string fam, string? value, bool clear)
    {
        if (!string.Equals(fam, "openSource", StringComparison.OrdinalIgnoreCase))
        {
            return "'kvCacheQuantization' is only valid for family=openSource.";
        }

        if (!clear && value is not ("f16" or "q8_0" or "q4_0"))
        {
            return "'kvCacheQuantization' expects 'f16', 'q8_0', 'q4_0', or 'null' to clear.";
        }

        config.Update(workspaceRoot, configScope, current =>
            ApplyAgentConfig(current, fam, a => a with { OpenSource = (a.OpenSource ?? new OpenSourceAgentConfig()) with { KvCacheQuantization = clear ? null : value } }));
        return clear
            ? $"Cleared override for 'openSource.kvCacheQuantization' ({configScope})."
            : $"Set 'openSource.kvCacheQuantization' = {value} ({configScope}).";
    }

    private string SetAgentField(string workspaceRoot, string? family, string? field, string? value, string? scope)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            return "set-agent requires 'field'.";
        }

        if (!TryParseScope(scope, out AiDevTools.TokenSaver.Config.Contracts.ConfigScope configScope))
        {
            return "set-agent requires 'scope' to be 'local' or 'global'.";
        }

        string fam = string.IsNullOrWhiteSpace(family) ? "global" : family.Trim();
        bool clear = value is null || value.Equals("null", StringComparison.OrdinalIgnoreCase);

        if (!IsValidFamily(fam))
        {
            return $"Unknown family '{fam}'. Use: global, anthropic, deepSeek, qwen, openSource.";
        }

        if (string.Equals(field, "kvCacheQuantization", StringComparison.OrdinalIgnoreCase))
        {
            return SetKvCacheQuantization(workspaceRoot, configScope, fam, value, clear);
        }

        bool isBool = AgentCommonBoolFields.Contains(field)
                      || (string.Equals(fam, "anthropic", StringComparison.OrdinalIgnoreCase) && AnthropicOnlyBoolFields.Contains(field))
                      || (string.Equals(fam, "openSource", StringComparison.OrdinalIgnoreCase) && OpenSourceOnlyBoolFields.Contains(field));
        bool isInt = AgentCommonIntFields.Contains(field)
                     || (string.Equals(fam, "anthropic", StringComparison.OrdinalIgnoreCase) && AnthropicOnlyIntFields.Contains(field))
                     || (string.Equals(fam, "openSource", StringComparison.OrdinalIgnoreCase) && OpenSourceOnlyIntFields.Contains(field));

        if (isBool)
        {
            if (!clear && !bool.TryParse(value, out _))
            {
                return $"'{field}' expects a boolean value (true/false) or 'null' to clear.";
            }

            bool? parsed = clear ? null : bool.Parse(value!);
            config.Update(workspaceRoot, configScope, current => ApplyAgentConfig(current, fam, a => ApplyAgentBoolField(a, fam, field, parsed)));
        }
        else if (isInt)
        {
            if (!clear && !int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out _))
            {
                return $"'{field}' expects an integer value or 'null' to clear.";
            }

            int? parsed = clear ? null : int.Parse(value!, System.Globalization.CultureInfo.InvariantCulture);
            config.Update(workspaceRoot, configScope, current => ApplyAgentConfig(current, fam, a => ApplyAgentIntField(a, fam, field, parsed)));
        }
        else
        {
            return $"Unknown field '{field}' for family '{fam}'. See ManageConfig's description for the list of settable agentConfig fields.";
        }

        return clear
            ? $"Cleared override for '{fam}.{field}' ({configScope})."
            : $"Set '{fam}.{field}' = {value} ({configScope}).";
    }

    private static bool IsValidFamily(string family) =>
        family.Equals("global", StringComparison.OrdinalIgnoreCase)
        || family.Equals("anthropic", StringComparison.OrdinalIgnoreCase)
        || family.Equals("deepSeek", StringComparison.OrdinalIgnoreCase)
        || family.Equals("qwen", StringComparison.OrdinalIgnoreCase)
        || family.Equals("openSource", StringComparison.OrdinalIgnoreCase);

    private static TokenSaverBehaviorConfig ApplyAgentConfig(
        TokenSaverBehaviorConfig c, string family, Func<AgentBehaviorConfig, AgentBehaviorConfig> mutate)
    {
        AgentBehaviorConfig agent = c.AgentConfig ?? new AgentBehaviorConfig();
        return c with { AgentConfig = mutate(agent) };
    }

    private static AgentBehaviorConfig ApplyAgentBoolField(AgentBehaviorConfig a, string family, string field, bool? v)
    {
        bool Is(string name) => string.Equals(field, name, StringComparison.OrdinalIgnoreCase);

        if (family.Equals("global", StringComparison.OrdinalIgnoreCase))
        {
            if (Is("enablePromptCaching")) return a with { EnablePromptCaching = v };
            return a;
        }

        if (family.Equals("anthropic", StringComparison.OrdinalIgnoreCase))
        {
            AnthropicAgentConfig s = a.Anthropic ?? new AnthropicAgentConfig();
            if (Is("enablePromptCaching")) return a with { Anthropic = s with { EnablePromptCaching = v } };
            if (Is("enableContextEditing")) return a with { Anthropic = s with { EnableContextEditing = v } };
            return a;
        }

        if (family.Equals("deepSeek", StringComparison.OrdinalIgnoreCase))
        {
            DeepSeekAgentConfig s = a.DeepSeek ?? new DeepSeekAgentConfig();
            if (Is("enablePromptCaching")) return a with { DeepSeek = s with { EnablePromptCaching = v } };
            return a;
        }

        if (family.Equals("qwen", StringComparison.OrdinalIgnoreCase))
        {
            QwenAgentConfig s = a.Qwen ?? new QwenAgentConfig();
            if (Is("enablePromptCaching")) return a with { Qwen = s with { EnablePromptCaching = v } };
            return a;
        }

        if (family.Equals("openSource", StringComparison.OrdinalIgnoreCase))
        {
            OpenSourceAgentConfig s = a.OpenSource ?? new OpenSourceAgentConfig();
            if (Is("enablePromptCaching")) return a with { OpenSource = s with { EnablePromptCaching = v } };
            if (Is("useFlashAttention")) return a with { OpenSource = s with { UseFlashAttention = v } };
            return a;
        }

        return a;
    }

    private static AgentBehaviorConfig ApplyAgentIntField(AgentBehaviorConfig a, string family, string field, int? v)
    {
        bool Is(string name) => string.Equals(field, name, StringComparison.OrdinalIgnoreCase);

        if (family.Equals("global", StringComparison.OrdinalIgnoreCase))
        {
            if (Is("cacheTtlSeconds")) return a with { CacheTtlSeconds = v };
            if (Is("minCacheableTokens")) return a with { MinCacheableTokens = v };
            return a;
        }

        if (family.Equals("anthropic", StringComparison.OrdinalIgnoreCase))
        {
            AnthropicAgentConfig s = a.Anthropic ?? new AnthropicAgentConfig();
            if (Is("cacheTtlSeconds")) return a with { Anthropic = s with { CacheTtlSeconds = v } };
            if (Is("minCacheableTokens")) return a with { Anthropic = s with { MinCacheableTokens = v } };
            if (Is("contextEditingTriggerTokens")) return a with { Anthropic = s with { ContextEditingTriggerTokens = v } };
            if (Is("contextEditingKeepToolUses")) return a with { Anthropic = s with { ContextEditingKeepToolUses = v } };
            return a;
        }

        if (family.Equals("qwen", StringComparison.OrdinalIgnoreCase))
        {
            QwenAgentConfig s = a.Qwen ?? new QwenAgentConfig();
            if (Is("cacheTtlSeconds")) return a with { Qwen = s with { CacheTtlSeconds = v } };
            if (Is("minCacheableTokens")) return a with { Qwen = s with { MinCacheableTokens = v } };
            return a;
        }

        if (family.Equals("openSource", StringComparison.OrdinalIgnoreCase))
        {
            OpenSourceAgentConfig s = a.OpenSource ?? new OpenSourceAgentConfig();
            if (Is("numCtx")) return a with { OpenSource = s with { NumCtx = v } };
            return a;
        }

        return a;
    }

    private static readonly HashSet<string> BoolFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "autoEnabled", "requireAnchorMatch", "enableDistill", "allowTerse",
        "enableRawRetention", "enableSavingsTracking", "enableSummaryEmbedding", "terseByDefault",
        "autoWarmOnStartup", "autoIntercept",
    };

    private static readonly HashSet<string> DoubleFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "canDoThreshold", "deferThreshold", "confidenceThreshold", "modelCallThreshold",
        "wasteThreshold", "learningRate", "tokenBudgetMargin", "semanticThreshold",
    };

    private static readonly HashSet<string> IntFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "contextSizeLimit",
    };

    private string SetField(string workspaceRoot, string? field, string? value, string? scope)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            return "set requires 'field'.";
        }

        if (!TryParseScope(scope, out AiDevTools.TokenSaver.Config.Contracts.ConfigScope configScope))
        {
            return "set requires 'scope' to be 'local' or 'global'.";
        }

        bool clear = value is null || value.Equals("null", StringComparison.OrdinalIgnoreCase);

        if (BoolFields.Contains(field))
        {
            if (!clear && !bool.TryParse(value, out _))
            {
                return $"'{field}' expects a boolean value (true/false) or 'null' to clear.";
            }

            bool? parsed = clear ? null : bool.Parse(value!);
            config.Update(workspaceRoot, configScope, current => ApplyBoolField(current, field, parsed));
        }
        else if (DoubleFields.Contains(field))
        {
            if (!clear && !double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out _))
            {
                return $"'{field}' expects a numeric value or 'null' to clear.";
            }

            double? parsed = clear ? null : double.Parse(value!, System.Globalization.CultureInfo.InvariantCulture);
            config.Update(workspaceRoot, configScope, current => ApplyDoubleField(current, field, parsed));
        }
        else if (IntFields.Contains(field))
        {
            if (!clear && !int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out _))
            {
                return $"'{field}' expects an integer value or 'null' to clear.";
            }

            int? parsed = clear ? null : int.Parse(value!, System.Globalization.CultureInfo.InvariantCulture);
            config.Update(workspaceRoot, configScope, current => ApplyIntField(current, field, parsed));
        }
        else
        {
            return $"Unknown field '{field}'. See ManageConfig's description for the list of settable fields.";
        }

        return clear
            ? $"Cleared override for '{field}' ({configScope}) — reverted to default."
            : $"Set '{field}' = {value} ({configScope}).";
    }

    private static TokenSaverBehaviorConfig ApplyBoolField(TokenSaverBehaviorConfig c, string field, bool? v)
    {
        bool Is(string name) => string.Equals(field, name, StringComparison.OrdinalIgnoreCase);

        CodeHelperBehaviorConfig ch = c.CodeHelper ?? new CodeHelperBehaviorConfig();
        TokenEconomyBehaviorConfig te = c.TokenEconomy ?? new TokenEconomyBehaviorConfig();
        ContextBehaviorConfig ctx = c.Context ?? new ContextBehaviorConfig();

        if (Is("autoEnabled")) return c with { CodeHelper = ch with { AutoEnabled = v } };
        if (Is("requireAnchorMatch")) return c with { CodeHelper = ch with { RequireAnchorMatch = v } };
        if (Is("enableDistill")) return c with { TokenEconomy = te with { EnableDistill = v } };
        if (Is("allowTerse")) return c with { TokenEconomy = te with { AllowTerse = v } };
        if (Is("enableRawRetention")) return c with { TokenEconomy = te with { EnableRawRetention = v } };
        if (Is("enableSavingsTracking")) return c with { TokenEconomy = te with { EnableSavingsTracking = v } };
        if (Is("enableSummaryEmbedding")) return c with { TokenEconomy = te with { EnableSummaryEmbedding = v } };
        if (Is("terseByDefault")) return c with { TokenEconomy = te with { TerseByDefault = v } };
        if (Is("autoWarmOnStartup")) return c with { Context = ctx with { AutoWarmOnStartup = v } };
        if (Is("autoIntercept")) return c with { Context = ctx with { AutoIntercept = v } };
        return c;
    }

    private static TokenSaverBehaviorConfig ApplyDoubleField(TokenSaverBehaviorConfig c, string field, double? v)
    {
        bool Is(string name) => string.Equals(field, name, StringComparison.OrdinalIgnoreCase);

        CodeHelperBehaviorConfig ch = c.CodeHelper ?? new CodeHelperBehaviorConfig();
        OwnershipThresholds ownership = ch.Ownership ?? new OwnershipThresholds();
        PlannerThresholds planner = ch.Planner ?? new PlannerThresholds();
        CalibrationTuning calibration = ch.Calibration ?? new CalibrationTuning();

        if (Is("canDoThreshold")) return c with { CodeHelper = ch with { Ownership = ownership with { CanDoThreshold = v } } };
        if (Is("deferThreshold")) return c with { CodeHelper = ch with { Ownership = ownership with { DeferThreshold = v } } };
        if (Is("confidenceThreshold")) return c with { CodeHelper = ch with { Planner = planner with { ConfidenceThreshold = v } } };
        if (Is("modelCallThreshold")) return c with { CodeHelper = ch with { Planner = planner with { ModelCallThreshold = v } } };
        if (Is("wasteThreshold")) return c with { CodeHelper = ch with { Calibration = calibration with { WasteThreshold = v } } };
        if (Is("learningRate")) return c with { CodeHelper = ch with { Calibration = calibration with { LearningRate = v } } };

        ContextBehaviorConfig ctx = c.Context ?? new ContextBehaviorConfig();
        if (Is("tokenBudgetMargin")) return c with { Context = ctx with { TokenBudgetMargin = v } };
        if (Is("semanticThreshold")) return c with { Context = ctx with { SemanticThreshold = v } };
        return c;
    }

    private static TokenSaverBehaviorConfig ApplyIntField(TokenSaverBehaviorConfig c, string field, int? v)
    {
        bool Is(string name) => string.Equals(field, name, StringComparison.OrdinalIgnoreCase);

        ContextBehaviorConfig ctx = c.Context ?? new ContextBehaviorConfig();
        if (Is("contextSizeLimit")) return c with { Context = ctx with { ContextSizeLimit = v } };
        return c;
    }

    private string GetConfig(string workspaceRoot)
    {
        TokenSaverBehaviorConfig merged = config.GetConfig(workspaceRoot);

        // Detect the current caller the same way AutoWorkflowTools/CodeHelperTools/
        // LocalModelHelperTools do, so "get" shows exactly what agentConfig settings actually
        // apply to this session — not just the raw per-family sections the user would otherwise
        // have to cross-reference by hand.
        AgentConfigResolution? detected = httpContext.HttpContext is { } hc
            ? agentConfigResolver.Resolve(hc, workspaceRoot)
            : null;
        LazyContextResolution? detectedLazy = httpContext.HttpContext is { } hc2
            ? lazyContextResolver.Resolve(hc2, workspaceRoot)
            : null;

        var result = new
        {
            merged,
            localPath = BehaviorConfigService.LocalConfigPath(workspaceRoot),
            globalPath = BehaviorConfigService.GlobalConfigPath,
            gitignorePromptPending = GitignorePrompt.ShouldAsk(config, workspaceRoot),
            detectedAgent = detected is null
                ? null
                : new
                {
                    family = detected.Family,
                    clientName = detected.Caller.ClientName,
                    modelHint = detected.Caller.ModelHint,
                    detectionSource = detected.Caller.Source.ToString(),
                    effectiveSettings = detected.Settings,
                },
            detectedLazyContext = detectedLazy is null
                ? null
                : new
                {
                    clientName = detectedLazy.Caller.ClientName,
                    willAskForMissingContext = detectedLazy.WillAskForMissingContext,
                    decidedBy = detectedLazy.Source.ToString(),
                },
        };
        return JsonSerializer.Serialize(result, JsonOpts);
    }

    private string SetGitignorePref(string workspaceRoot, bool? add, string? scope)
    {
        if (add is not bool value)
        {
            return "set-gitignore-pref requires 'add' (true or false).";
        }

        if (!TryParseScope(scope, out AiDevTools.TokenSaver.Config.Contracts.ConfigScope configScope))
        {
            return "set-gitignore-pref requires 'scope' to be 'local' or 'global'.";
        }

        config.Update(workspaceRoot, configScope, current => current with
        {
            RepoHelper = (current.RepoHelper ?? new RepoHelperBehaviorConfig()) with { AddTokenSaverToGitignore = value },
        });

        string note;
        if (value && !GitignoreFilter.EntryIsCovered(workspaceRoot, GitignorePrompt.Entry))
        {
            GitignoreFilter.AddEntries(workspaceRoot, [(GitignorePrompt.Entry, false, "TokenSaver cache")]);
            note = $"Recorded ({configScope}) and added `{GitignorePrompt.Entry}/` to .gitignore.";
        }
        else
        {
            note = value
                ? $"Recorded ({configScope}); `{GitignorePrompt.Entry}` was already covered by .gitignore."
                : $"Recorded ({configScope}); will not add `{GitignorePrompt.Entry}` to .gitignore.";
        }

        return note;
    }

    private static string AppendIgnore(string filePath, string[]? paths, string label)
    {
        if (paths is null || paths.Length == 0)
        {
            return $"No patterns supplied. Pass paths[] with the {label} patterns to append.";
        }

        IReadOnlyList<string> added = WorkspaceIgnoreFilter.Append(filePath, paths);
        return added.Count == 0
            ? $"Nothing added — all supplied patterns already present in {Path.GetFileName(filePath)}."
            : $"Added {added.Count} {label} pattern(s) to {Path.GetFileName(filePath)}: {string.Join(", ", added)}.";
    }

    private static bool TryParseScope(string? scope, out AiDevTools.TokenSaver.Config.Contracts.ConfigScope configScope)
    {
        switch (scope?.Trim().ToUpperInvariant())
        {
            case "LOCAL":
                configScope = AiDevTools.TokenSaver.Config.Contracts.ConfigScope.Local;
                return true;
            case "GLOBAL":
                configScope = AiDevTools.TokenSaver.Config.Contracts.ConfigScope.Global;
                return true;
            default:
                configScope = AiDevTools.TokenSaver.Config.Contracts.ConfigScope.Local;
                return false;
        }
    }
}
