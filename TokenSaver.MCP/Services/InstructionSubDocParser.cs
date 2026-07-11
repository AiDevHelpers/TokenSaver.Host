using System.Text.RegularExpressions;

namespace TokenSaver.MCP.Services;

/// <summary>
///     Frontmatter extracted from a sub-doc rule file: <c>paths</c> (the glob list Claude Code's own
///     lazy-rule loader matches against — see <c>.claude/rules/*.md</c> in this repo for the
///     convention) and <c>taskTypes</c> (an additive, TokenSaver-specific field — a list of
///     <see cref="TokenSaver.ContextHelper.Models.TaskType" /> names — that opts a sub-doc into
///     proactive injection by <see cref="InstructionCacheService.TryGetInjectableDoc" /> when
///     <c>PromptAssessor</c> classifies a request as that task type, independent of whether the
///     request touches a matching file path).
/// </summary>
public sealed record SubDocFrontMatter(IReadOnlyList<string> PathGlobs, IReadOnlyList<string> TaskTypes)
{
    public static readonly SubDocFrontMatter Empty = new([], []);
}

/// <summary>A sub-doc discovered by following backtick-wrapped <c>.md</c> references out of a main instructions doc.</summary>
public sealed record DiscoveredSubDoc(
    string RelativeSourcePath,
    string Slug,
    string RawContent,
    string BodyContent,
    SubDocFrontMatter FrontMatter);

/// <summary>
///     Discovers and parses the sub-docs a main instructions file (e.g. the global
///     <c>CLAUDE.md</c>) points to via backtick-wrapped relative <c>.md</c> paths — the same
///     "loads on demand" convention this repo's own CLAUDE.md uses to reference
///     <c>.claude/rules/*.md</c>. No new link syntax is invented; any existing backtick reference
///     to an on-disk <c>.md</c> file is treated as a lazy-loaded sub-doc.
/// </summary>
public static partial class InstructionSubDocParser
{
    [GeneratedRegex(@"`([^`\n]+\.md)`")]
    private static partial Regex MarkdownReferencePattern();

    /// <summary>
    ///     Finds every backtick-wrapped <c>.md</c> reference in <paramref name="mainDocContent" />
    ///     that resolves to a file that actually exists (relative to <paramref name="mainDocPath" />'s
    ///     directory, expanding a leading <c>~</c> to the user profile first), reading and parsing
    ///     each one. References that don't resolve to an existing file are silently skipped — most
    ///     backtick <c>.md</c> mentions in prose aren't meant as sub-doc links.
    /// </summary>
    /// <param name="seenPaths">
    ///     Shared across-call dedup set (resolved absolute paths) — pass the same instance when
    ///     discovering sub-docs from multiple main docs in one chain (see
    ///     <see cref="InstructionCacheService" />'s walk-up-the-directory-tree discovery) so a
    ///     sub-doc referenced from more than one level is only ever processed once. Omit for a
    ///     single standalone call.
    /// </param>
    /// <param name="seenSlugs">Shared across-call counterpart to <paramref name="seenPaths" /> for slug uniqueness.</param>
    public static IReadOnlyList<DiscoveredSubDoc> DiscoverSubDocs(
        string mainDocPath, string mainDocContent, HashSet<string>? seenPaths = null, HashSet<string>? seenSlugs = null)
    {
        string? mainDir = Path.GetDirectoryName(mainDocPath);
        if (string.IsNullOrEmpty(mainDir))
        {
            return [];
        }

        List<DiscoveredSubDoc> results = [];
        seenPaths ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        seenSlugs ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in MarkdownReferencePattern().Matches(mainDocContent))
        {
            string rawRef = match.Groups[1].Value.Trim();
            string expanded = ResolveHomeClaudeReference(rawRef, mainDir);
            string resolved = Path.IsPathFullyQualified(expanded)
                ? Path.GetFullPath(expanded)
                : Path.GetFullPath(Path.Combine(mainDir, expanded));

            if (!seenPaths.Add(resolved) || !File.Exists(resolved))
            {
                continue;
            }

            string raw = File.ReadAllText(resolved);
            (SubDocFrontMatter frontMatter, string body) = ParseFrontMatter(raw);
            string relative = Path.GetRelativePath(mainDir, resolved).Replace('\\', '/');
            string slug = UniqueSlug(relative, seenSlugs);

            results.Add(new DiscoveredSubDoc(relative, slug, raw, body, frontMatter));
        }

        return results;
    }

    /// <summary>
    ///     Resolves a <c>~/.claude/...</c> (or <c>~\.claude\...</c>) reference against
    ///     <paramref name="mainDir" /> — the main doc's own directory — rather than the real
    ///     <see cref="Environment.SpecialFolder.UserProfile" />. <paramref name="mainDir" /> already
    ///     <em>is</em> the <c>.claude</c>-equivalent directory in both the real-user case (main doc
    ///     lives at <c>%USERPROFILE%\.claude\CLAUDE.md</c>) and the
    ///     <c>TOKENSAVER_GLOBAL_CLAUDE_MD_PATH</c>-overridden case (tests point it at a temp
    ///     directory standing in for <c>.claude</c>) — so stripping the <c>~/.claude/</c> prefix and
    ///     combining the remainder with <paramref name="mainDir" /> gives the correct answer either
    ///     way, without ever touching the real user profile when it isn't the source of truth. Any
    ///     other <c>~/...</c> reference (rare — a sub-doc link outside <c>.claude</c>) still expands
    ///     against the real user profile.
    /// </summary>
    private static string ResolveHomeClaudeReference(string path, string mainDir)
    {
        const string homeClaudeUnix = "~/.claude/";
        const string homeClaudeWindows = "~\\.claude\\";

        if (path.StartsWith(homeClaudeUnix, StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(mainDir, path[homeClaudeUnix.Length..]);
        }

        if (path.StartsWith(homeClaudeWindows, StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(mainDir, path[homeClaudeWindows.Length..]);
        }

        return ExpandHome(path);
    }

    private static string ExpandHome(string path)
    {
        if (path.Length < 2 || path[0] != '~' || (path[1] != '/' && path[1] != '\\'))
        {
            return path;
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, path[2..]);
    }

    private static string UniqueSlug(string relativePath, HashSet<string> seenSlugs)
    {
        string baseSlug = Path.GetFileNameWithoutExtension(relativePath);
        string slug = baseSlug;
        int suffix = 2;
        while (!seenSlugs.Add(slug))
        {
            slug = $"{baseSlug}-{suffix++}";
        }

        return slug;
    }

    /// <summary>
    ///     Parses a leading <c>---</c>-delimited YAML frontmatter block for <c>paths:</c> and
    ///     <c>taskTypes:</c> list entries. Deliberately a minimal line-based parser (matching the
    ///     small, hand-authored subset this repo's own rule files use) rather than a full YAML
    ///     library — anything else in the block is ignored.
    /// </summary>
    internal static (SubDocFrontMatter FrontMatter, string Body) ParseFrontMatter(string raw)
    {
        string[] lines = raw.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            return (SubDocFrontMatter.Empty, raw);
        }

        int endIndex = -1;
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---")
            {
                endIndex = i;
                break;
            }
        }

        if (endIndex < 0)
        {
            return (SubDocFrontMatter.Empty, raw);
        }

        List<string> pathGlobs = [];
        List<string> taskTypes = [];
        string? currentKey = null;

        for (int i = 1; i < endIndex; i++)
        {
            string line = lines[i];
            string trimmed = line.TrimStart();

            if (trimmed.StartsWith("- ", StringComparison.Ordinal) && currentKey is not null)
            {
                string value = trimmed[2..].Trim().Trim('"');
                if (value.Length > 0)
                {
                    (currentKey == "paths" ? pathGlobs : taskTypes).Add(value);
                }

                continue;
            }

            int colon = trimmed.IndexOf(':', StringComparison.Ordinal);
            currentKey = colon > 0 && trimmed[..colon].Trim() is "paths" or "taskTypes"
                ? trimmed[..colon].Trim()
                : null;
        }

        string body = string.Join('\n', lines[(endIndex + 1)..]).TrimStart('\n');
        return (new SubDocFrontMatter(pathGlobs, taskTypes), body);
    }
}
