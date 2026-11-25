using System.Text;
using System.Text.RegularExpressions;

namespace AiDevTools.TokenSaver.MCP.Services;

/// <summary>
///     Serves the editable instruction store: one folder per skill, each holding a <c>SKILL.md</c>
///     whose YAML-style frontmatter (<c>name</c>, <c>description</c>) identifies it. The store is
///     read from <see cref="DefaultRoot" /> (<c>TOKENSAVER_INSTRUCTIONS_DIR</c> or
///     <c>%USERPROFILE%\.tokensaver\instructions\skills</c>), which the installer populates from the
///     repo's shipped defaults and the user is free to edit.
/// </summary>
internal sealed class InstructionStoreService
{
    internal const string EnvVarName = "TOKENSAVER_INSTRUCTIONS_DIR";
    internal const string SkillFileName = "SKILL.md";

    private static readonly Regex NamePattern = new("^[a-z0-9][a-z0-9-]*$", RegexOptions.Compiled);

    private readonly string _root;

    public InstructionStoreService(string root)
    {
        _root = root;
    }

    /// <summary>Store location: <c>TOKENSAVER_INSTRUCTIONS_DIR</c> when set, else the user-profile default.</summary>
    public static string DefaultRoot =>
        Environment.GetEnvironmentVariable(EnvVarName) is { Length: > 0 } overridePath
            ? overridePath
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tokensaver", "instructions", "skills");

    /// <summary>Every instruction in the store, sorted by name. Empty when the store folder does not exist.</summary>
    public IReadOnlyList<InstructionInfo> List()
    {
        if (!Directory.Exists(_root))
        {
            return [];
        }

        List<InstructionInfo> items = [];
        foreach (string dir in Directory.EnumerateDirectories(_root))
        {
            string file = Path.Combine(dir, SkillFileName);
            if (!File.Exists(file))
            {
                continue;
            }

            (string? name, string? description) = ParseFrontmatter(File.ReadAllText(file));
            items.Add(new InstructionInfo(name ?? Path.GetFileName(dir), description ?? ""));
        }

        items.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return items;
    }

    /// <summary>
    ///     Full text of the instruction <paramref name="name" /> (frontmatter included), or
    ///     <see langword="null" /> when the name is invalid or no such instruction exists.
    /// </summary>
    public string? Get(string name)
    {
        if (!NamePattern.IsMatch(name))
        {
            return null;
        }

        string file = Path.Combine(_root, name, SkillFileName);
        return File.Exists(file) ? File.ReadAllText(file) : null;
    }

    /// <summary>
    ///     Injection is on unless <c>TOKENSAVER_INSTRUCTIONS_INJECT=off</c>. On-demand tools always work.
    /// </summary>
    public static bool InjectionEnabled =>
        !string.Equals(Environment.GetEnvironmentVariable(InjectEnvVarName), "off", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Returns the instruction sections relevant to <paramref name="prompt" />, each tagged with
    ///     its source, capped at <paramref name="maxChars" />. Heuristic: a section scores one point
    ///     per distinct prompt term in its body and two more per term in its heading, and is kept only
    ///     at <see cref="MinSectionScore" /> or above. Returns an empty string when nothing matches.
    /// </summary>
    public string SelectRelevantSections(string prompt, int maxChars = 4000)
    {
        HashSet<string> terms = PromptTerms(prompt);
        if (terms.Count == 0)
        {
            return "";
        }

        List<(int Score, string Source, string Text)> hits = [];
        foreach (InstructionInfo info in List())
        {
            string? full = Get(info.Name);
            if (full is null)
            {
                continue;
            }

            foreach (InstructionSection section in SplitSections(StripFrontmatter(full)))
            {
                int score = ScoreSection(section, terms);
                if (score >= MinSectionScore)
                {
                    hits.Add((score, $"{info.Name} > {section.Heading}", section.Text));
                }
            }
        }

        hits.Sort((a, b) => b.Score.CompareTo(a.Score));

        StringBuilder sb = new();
        foreach ((int _, string source, string text) in hits)
        {
            string block = $"<!-- instruction: {source} -->\n{text.Trim()}\n\n";
            if (sb.Length + block.Length > maxChars)
            {
                break;
            }

            _ = sb.Append(block);
        }

        return sb.ToString().TrimEnd();
    }

    internal const string InjectEnvVarName = "TOKENSAVER_INSTRUCTIONS_INJECT";
    internal const int MinSectionScore = 3;

    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "after", "before", "could", "from", "have", "into", "make", "need", "should",
        "that", "their", "them", "then", "there", "this", "what", "when", "where", "which",
        "will", "with", "would", "your", "please", "help", "want", "just", "like", "some",
    };

    internal sealed record InstructionSection(string Heading, string Text);

    internal static HashSet<string> PromptTerms(string prompt)
    {
        HashSet<string> terms = new(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(prompt, "[a-z][a-z0-9]{3,}", RegexOptions.IgnoreCase))
        {
            string term = m.Value;
            if (term.Length > 4 && term.EndsWith('s'))
            {
                term = term[..^1];
            }

            if (!Stopwords.Contains(term))
            {
                _ = terms.Add(term);
            }
        }

        return terms;
    }

    internal static int ScoreSection(InstructionSection section, HashSet<string> terms)
    {
        string body = section.Text;
        string heading = section.Heading;
        int score = 0;
        foreach (string term in terms)
        {
            // Word-start match: "test" must not match inside "lightest".
            Regex wordStart = new(@"\b" + Regex.Escape(term), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (wordStart.IsMatch(body))
            {
                score += 1;
            }

            if (wordStart.IsMatch(heading))
            {
                score += 2;
            }
        }

        return score;
    }

    internal static IReadOnlyList<InstructionSection> SplitSections(string body)
    {
        List<InstructionSection> sections = [];
        string heading = "(intro)";
        StringBuilder current = new();
        foreach (string line in body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith("# ", StringComparison.Ordinal) || line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (current.Length > 0)
                {
                    sections.Add(new InstructionSection(heading, current.ToString()));
                }

                _ = current.Clear();
                heading = line.TrimStart('#', ' ');
            }

            _ = current.AppendLine(line);
        }

        if (current.Length > 0)
        {
            sections.Add(new InstructionSection(heading, current.ToString()));
        }

        return sections;
    }

    internal static string StripFrontmatter(string text)
    {
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            return normalized;
        }

        int end = normalized.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            return normalized;
        }

        return normalized[(end + 4)..].TrimStart('\n');
    }

    internal static (string? Name, string? Description) ParseFrontmatter(string text)
    {
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            return (null, null);
        }

        int end = normalized.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            return (null, null);
        }

        string? name = null;
        string? description = null;
        foreach (string line in normalized[4..end].Split('\n'))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            string key = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim().Trim('"', '\'');
            if (key == "name")
            {
                name = value;
            }
            else if (key == "description")
            {
                description = value;
            }
        }

        return (name, description);
    }
}

/// <summary>One entry in the instruction store.</summary>
internal sealed record InstructionInfo(string Name, string Description);
