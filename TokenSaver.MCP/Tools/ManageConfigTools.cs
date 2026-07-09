using TokenSaver.CodeHelper.Indexing;
using TokenSaver.Config;
using TokenSaver.Library.Services;

using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Text.Json;

namespace TokenSaver.MCP.Tools;

[McpServerToolType]
internal sealed class ManageConfigTools(
    BehaviorConfigService config,
    WorkspaceConfigManager workspaceConfig)
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
        "Boolean fields: autoCodeHelperEnabled, requireAnchorMatch, enableDistill, enableTerse, " +
        "enableRawRetention, enableSavingsTracking, enableSummaryEmbedding, terse. " +
        "Numeric fields: canDoThreshold, deferThreshold, confidenceThreshold, modelCallThreshold, " +
        "wasteThreshold, learningRate. Pass value=null to clear the override and revert to the default.")]
    public string ManageConfig(
        [Description("Action: 'get', 'set-gitignore-pref', 'ignore-additional', 'ignore-bypass', or 'set'")]
        string action,
        [Description("For 'set-gitignore-pref': true to add .tokensaver/ to .gitignore, false to decline")]
        bool? add = null,
        [Description("For 'set-gitignore-pref'/'set': 'local' (this workspace) or 'global' (all workspaces on this machine)")]
        string? scope = null,
        [Description("For 'ignore-additional'/'ignore-bypass': gitignore-syntax patterns to append")]
        string[]? paths = null,
        [Description("Workspace root override; defaults to the configured workspace root")]
        string? root = null,
        [Description("For 'set': the behavior-config field name, e.g. 'autoCodeHelperEnabled' or 'wasteThreshold'")]
        string? field = null,
        [Description("For 'set': the new value as a string (parsed per field type), or omit/'null' to clear the override")]
        string? value = null)
    {
        string workspaceRoot = root ?? workspaceConfig.GetWorkspaceRoot();

        return action switch
        {
            "get" => GetConfig(workspaceRoot),
            "set-gitignore-pref" => SetGitignorePref(workspaceRoot, add, scope),
            "ignore-additional" => AppendIgnore(WorkspaceIgnoreFilter.AdditionalPath(workspaceRoot), paths, "additional exclude"),
            "ignore-bypass" => AppendIgnore(WorkspaceIgnoreFilter.BypassPath(workspaceRoot), paths, "bypass (force-include)"),
            "set" => SetField(workspaceRoot, field, value, scope),
            _ => $"Unknown action '{action}'. Use: get, set-gitignore-pref, ignore-additional, ignore-bypass, set.",
        };
    }

    private static readonly HashSet<string> BoolFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "autoCodeHelperEnabled", "requireAnchorMatch", "enableDistill", "enableTerse",
        "enableRawRetention", "enableSavingsTracking", "enableSummaryEmbedding", "terse",
    };

    private static readonly HashSet<string> DoubleFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "canDoThreshold", "deferThreshold", "confidenceThreshold", "modelCallThreshold",
        "wasteThreshold", "learningRate",
    };

    private string SetField(string workspaceRoot, string? field, string? value, string? scope)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            return "set requires 'field'.";
        }

        if (!TryParseScope(scope, out ConfigScope configScope))
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
            config.Set(workspaceRoot, configScope, current => ApplyBoolField(current, field, parsed));
        }
        else if (DoubleFields.Contains(field))
        {
            if (!clear && !double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out _))
            {
                return $"'{field}' expects a numeric value or 'null' to clear.";
            }

            double? parsed = clear ? null : double.Parse(value!, System.Globalization.CultureInfo.InvariantCulture);
            config.Set(workspaceRoot, configScope, current => ApplyDoubleField(current, field, parsed));
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

        if (Is("autoCodeHelperEnabled")) return c with { AutoCodeHelperEnabled = v };
        if (Is("requireAnchorMatch")) return c with { RequireAnchorMatch = v };
        if (Is("enableDistill")) return c with { EnableDistill = v };
        if (Is("enableTerse")) return c with { EnableTerse = v };
        if (Is("enableRawRetention")) return c with { EnableRawRetention = v };
        if (Is("enableSavingsTracking")) return c with { EnableSavingsTracking = v };
        if (Is("enableSummaryEmbedding")) return c with { EnableSummaryEmbedding = v };
        if (Is("terse")) return c with { Terse = v };
        return c;
    }

    private static TokenSaverBehaviorConfig ApplyDoubleField(TokenSaverBehaviorConfig c, string field, double? v)
    {
        bool Is(string name) => string.Equals(field, name, StringComparison.OrdinalIgnoreCase);

        if (Is("canDoThreshold")) return c with { CanDoThreshold = v };
        if (Is("deferThreshold")) return c with { DeferThreshold = v };
        if (Is("confidenceThreshold")) return c with { ConfidenceThreshold = v };
        if (Is("modelCallThreshold")) return c with { ModelCallThreshold = v };
        if (Is("wasteThreshold")) return c with { WasteThreshold = v };
        if (Is("learningRate")) return c with { LearningRate = v };
        return c;
    }

    private string GetConfig(string workspaceRoot)
    {
        TokenSaverBehaviorConfig merged = config.GetConfig(workspaceRoot);
        var result = new
        {
            merged,
            localPath = BehaviorConfigService.LocalConfigPath(workspaceRoot),
            globalPath = BehaviorConfigService.GlobalConfigPath,
            gitignorePromptPending = GitignorePrompt.ShouldAsk(config, workspaceRoot),
        };
        return JsonSerializer.Serialize(result, JsonOpts);
    }

    private string SetGitignorePref(string workspaceRoot, bool? add, string? scope)
    {
        if (add is not bool value)
        {
            return "set-gitignore-pref requires 'add' (true or false).";
        }

        if (!TryParseScope(scope, out ConfigScope configScope))
        {
            return "set-gitignore-pref requires 'scope' to be 'local' or 'global'.";
        }

        config.Set(workspaceRoot, configScope, current => current with { AddTokenSaverToGitignore = value });

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

    private static bool TryParseScope(string? scope, out ConfigScope configScope)
    {
        switch (scope?.Trim().ToLowerInvariant())
        {
            case "local":
                configScope = ConfigScope.Local;
                return true;
            case "global":
                configScope = ConfigScope.Global;
                return true;
            default:
                configScope = ConfigScope.Local;
                return false;
        }
    }
}
