namespace TokenSaver.MCP.Services;

/// <summary>
///     Decides, for a given MCP <c>ClientName</c>, whether and where to sync the machine-global
///     Claude instructions (<c>%USERPROFILE%\.claude\CLAUDE.md</c>) into a workspace instruction
///     file — see <see cref="InstructionOptimizerService" />, which is the only caller.
///     <para>
///     Mirrors <see cref="LazyContextResolver" />'s shape (hardcoded per-known-client lookup, no
///     adapter/config layer needed since this is researched tool capability, not a judgment call):
///     each recognized client is mapped to the most token-efficient instruction mechanism that
///     tool actually supports as of 2026 — a scoped/glob-conditional rule file where the tool has
///     one, a single flat instruction file otherwise. Sources: Cursor's <c>.cursor/rules/*.mdc</c>
///     (glob/<c>alwaysApply</c> frontmatter, the modern replacement for flat <c>.cursorrules</c>);
///     GitHub Copilot's <c>.github/instructions/*.instructions.md</c> (<c>applyTo</c> glob
///     frontmatter, added mid-2025 alongside the older monolithic
///     <c>.github/copilot-instructions.md</c>); Continue.dev's <c>.continue/rules/*.md</c>
///     (<c>globs</c> frontmatter). Claude Code already natively auto-loads the real
///     <c>%USERPROFILE%\.claude\CLAUDE.md</c> into every session for every repo on this machine —
///     copying it into the repo too would just be a redundant duplicate, so Claude Code resolves
///     to "don't sync" rather than a target file.
///     </para>
/// </summary>
public static class InstructionSyncResolver
{
    private static readonly HashSet<string> ClaudeCodeClientNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "claude-code", "claude-ai", "claude", "claude-desktop", "cowork",
    };

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

    /// <summary>Resolves the sync target for <paramref name="clientName" />, workspace-root-relative.</summary>
    public static InstructionSyncTarget Resolve(string? clientName)
    {
        if (string.IsNullOrEmpty(clientName))
        {
            return InstructionSyncTarget.Unrecognized;
        }

        if (ClaudeCodeClientNames.Contains(clientName))
        {
            return InstructionSyncTarget.NativelyLoaded;
        }

        if (CursorClientNames.Contains(clientName))
        {
            return new InstructionSyncTarget(
                ShouldSync: true,
                RelativeFilePath: Path.Combine(".cursor", "rules", "tokensaver-global.mdc"),
                FrontMatter: "---\nalwaysApply: true\n---\n\n",
                Reason: "Cursor: .cursor/rules/*.mdc (scoped rule file)");
        }

        if (CopilotClientNames.Contains(clientName))
        {
            return new InstructionSyncTarget(
                ShouldSync: true,
                RelativeFilePath: Path.Combine(".github", "instructions", "tokensaver-global.instructions.md"),
                FrontMatter: "---\napplyTo: \"**\"\n---\n\n",
                Reason: "GitHub Copilot: .github/instructions/*.instructions.md (applyTo-scoped)");
        }

        if (ContinueClientNames.Contains(clientName))
        {
            return new InstructionSyncTarget(
                ShouldSync: true,
                RelativeFilePath: Path.Combine(".continue", "rules", "tokensaver-global.md"),
                FrontMatter: "---\nglobs: \"**/*\"\n---\n\n",
                Reason: "Continue.dev: .continue/rules/*.md (globs-scoped)");
        }

        return InstructionSyncTarget.Unrecognized;
    }

    /// <summary>
    ///     Resolves the sync target for one sub-doc (a rule file the main doc lazy-loads on
    ///     demand — see <see cref="InstructionSubDocParser" />), translating its <c>paths</c>
    ///     frontmatter into whatever glob field <paramref name="clientName" />'s own rule format
    ///     uses, so the client's native lazy-loading still applies to it. Returns <see langword="null" />
    ///     when the main doc itself isn't synced for this client (natively-loaded Claude Code) or
    ///     when the client has no scoped-rule mechanism to preserve the split into (unrecognized
    ///     clients fall back to one flat <c>AGENTS.md</c> for the main doc — fragmenting that into
    ///     per-sub-doc files would abandon the one convention they do support).
    /// </summary>
    public static InstructionSyncTarget? ResolveSubDoc(string? clientName, string slug, IReadOnlyList<string> pathGlobs)
    {
        InstructionSyncTarget main = Resolve(clientName);
        if (!main.ShouldSync || main.RelativeFilePath is null)
        {
            return null;
        }

        string? dir = Path.GetDirectoryName(main.RelativeFilePath);

        if (CursorClientNames.Contains(clientName!))
        {
            string frontMatter = pathGlobs.Count > 0
                ? $"---\nglobs:\n{YamlList(pathGlobs)}\nalwaysApply: false\n---\n\n"
                : "---\nalwaysApply: false\n---\n\n";
            return new InstructionSyncTarget(
                true, Path.Combine(dir!, $"tokensaver-{slug}.mdc"), frontMatter,
                $"Cursor: .cursor/rules/{slug}.mdc (scoped rule file)");
        }

        if (CopilotClientNames.Contains(clientName!))
        {
            string applyTo = pathGlobs.Count > 0 ? string.Join(",", pathGlobs) : "**";
            return new InstructionSyncTarget(
                true, Path.Combine(dir!, $"tokensaver-{slug}.instructions.md"), $"---\napplyTo: \"{applyTo}\"\n---\n\n",
                $"GitHub Copilot: .github/instructions/{slug}.instructions.md (applyTo-scoped)");
        }

        if (ContinueClientNames.Contains(clientName!))
        {
            string globs = pathGlobs.Count > 0
                ? $"[{string.Join(", ", pathGlobs.Select(g => $"\"{g}\""))}]"
                : "\"**/*\"";
            return new InstructionSyncTarget(
                true, Path.Combine(dir!, $"tokensaver-{slug}.md"), $"---\nglobs: {globs}\n---\n\n",
                $"Continue.dev: .continue/rules/{slug}.md (globs-scoped)");
        }

        // Unrecognized clients get one flat AGENTS.md for the main doc and nothing else — there's
        // no scoped-rule convention to preserve, so a separate per-sub-doc file would just be an
        // orphan nothing ever reads.
        return null;
    }

    private static string YamlList(IReadOnlyList<string> items) =>
        string.Join('\n', items.Select(i => $"  - \"{i}\""));
}

/// <summary>
///     Result of <see cref="InstructionSyncResolver.Resolve" />. When <see cref="ShouldSync" /> is
///     true, <see cref="RelativeFilePath" /> is workspace-root-relative and
///     <see cref="FrontMatter" /> (if any) is prepended before the synced content.
/// </summary>
public sealed record InstructionSyncTarget(bool ShouldSync, string? RelativeFilePath, string? FrontMatter, string Reason)
{
    /// <summary>Claude Code already auto-loads the real global CLAUDE.md itself — nothing to copy.</summary>
    public static readonly InstructionSyncTarget NativelyLoaded =
        new(false, null, null, "Claude Code already natively loads the global CLAUDE.md — skipping");

    /// <summary>
    ///     No researched scoped mechanism for this client — fall back to the emerging cross-tool
    ///     flat convention (<c>AGENTS.md</c> at the workspace root) rather than guessing at a
    ///     tool-specific path.
    /// </summary>
    public static readonly InstructionSyncTarget Unrecognized =
        new(true, "AGENTS.md", null, "Unrecognized client — falling back to flat AGENTS.md");
}
