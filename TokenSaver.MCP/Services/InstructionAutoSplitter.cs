using Microsoft.Extensions.Logging;

using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace TokenSaver.MCP.Services;

/// <summary>
///     Splits oversized <c>##</c> sections out of a <c>CLAUDE.md</c>'s content into synthetic
///     sub-docs, purely in memory — the real source file on disk is never read back into or
///     written from here, only the string content already loaded by
///     <see cref="InstructionCacheService" /> is transformed. Gated behind
///     <c>repoHelper.autoSplitOversizedInstructions</c> (on by default). Extracted sections flow
///     into the same <c>.tokensaver/context/instructions/subdocs/</c> cache and per-client sync
///     targets (AGENTS.md, <c>.cursor/rules/*.mdc</c>, etc.) as any backtick-referenced sub-doc —
///     those are already derived artifacts, never the user's own file. This only affects what
///     TokenSaver builds and sends to a connecting AI client; the user's hand-authored
///     <c>CLAUDE.md</c> (and the real project's own <c>.claude/rules/</c> directory) are never
///     touched.
///     <para>
///     Deliberately simple and conservative: only whole top-level (<c>##</c>) sections are ever
///     extracted, never partial sections or arbitrary paragraphs — splitting mid-thought would
///     produce a nonsensical sub-doc. A section only moves if it's individually large enough to
///     be worth the indirection (<see cref="SectionSizeThreshold" />), and the whole doc is only
///     considered at all once it crosses <see cref="DocSizeThreshold" /> — a short, focused
///     CLAUDE.md is left alone entirely.
///     </para>
/// </summary>
public static partial class InstructionAutoSplitter
{
    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Auto-split {DocPath} for caching: extracted section \"{Heading}\" as sub-doc {Slug}")]
    private static partial void LogSectionExtracted(ILogger logger, string docPath, string heading, string slug);

    /// <summary>Docs shorter than this are left untouched — not worth considering for a split at all.</summary>
    private const int DocSizeThreshold = 4000;

    /// <summary>A <c>##</c> section shorter than this stays inline — extracting it would just add indirection for little savings.</summary>
    private const int SectionSizeThreshold = 800;

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonSlugCharacters();

    /// <summary>
    ///     Returns <paramref name="content" /> unchanged (with no extracted sections) if
    ///     <paramref name="docPath" /> is under <see cref="DocSizeThreshold" /> or has no section
    ///     big enough to extract. Otherwise returns the shrunk content (each extracted section
    ///     replaced by a one-line pointer, in memory only) alongside the sections themselves as
    ///     <see cref="DiscoveredSubDoc" />s, ready to merge into the same sub-doc list ordinary
    ///     backtick-referenced sub-docs populate.
    /// </summary>
    /// <param name="seenSlugs">
    ///     Shared across the whole chain (see <c>InstructionCacheService.EnsureFresh</c>) so an
    ///     auto-split section's slug never collides with an ordinary discovered sub-doc's.
    /// </param>
    public static (string Content, IReadOnlyList<DiscoveredSubDoc> Sections) Split(
        string docPath, string content, HashSet<string> seenSlugs, ILogger logger)
    {
        if (content.Length < DocSizeThreshold)
        {
            return (content, []);
        }

        List<Section> sections = ParseSections(content, out List<string> preamble);
        if (sections.Count == 0)
        {
            return (content, []);
        }

        List<string> rebuilt = [.. preamble];
        List<DiscoveredSubDoc> extracted = [];
        string docFileName = Path.GetFileName(docPath);

        foreach (Section section in sections)
        {
            string sectionText = string.Join('\n', section.Lines);
            if (sectionText.Length < SectionSizeThreshold)
            {
                rebuilt.Add(sectionText);
                continue;
            }

            string slug = UniqueSlug(section.Heading, seenSlugs);
            string body = string.Join('\n', section.Lines.Skip(1)).Trim('\n').TrimEnd();

            extracted.Add(new DiscoveredSubDoc(
                RelativeSourcePath: $"{docFileName}#{section.Heading}",
                Slug: slug,
                RawContent: sectionText,
                BodyContent: $"## {section.Heading}\n\n{body}\n",
                FrontMatter: SubDocFrontMatter.Empty));

            rebuilt.Add($"## {section.Heading}\nDetailed guidance lives in the \"{section.Heading}\" sub-doc, loaded on demand.");
            LogSectionExtracted(logger, docPath, section.Heading, slug);
        }

        if (extracted.Count == 0)
        {
            return (content, []);
        }

        string shrunk = string.Join('\n', rebuilt).TrimEnd() + "\n";
        return (shrunk, extracted);
    }

    private sealed record Section(string Heading, List<string> Lines);

    private static List<Section> ParseSections(string content, out List<string> preamble)
    {
        string[] lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        List<Section> sections = [];
        preamble = [];
        Section? current = null;

        foreach (string line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (current is not null)
                {
                    sections.Add(current);
                }

                current = new Section(line[3..].Trim(), [line]);
            }
            else if (current is null)
            {
                preamble.Add(line);
            }
            else
            {
                current.Lines.Add(line);
            }
        }

        if (current is not null)
        {
            sections.Add(current);
        }

        return sections;
    }

    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase",
        Justification = "Lowercasing here produces a conventional lowercase slug for the cached " +
                         "sub-doc file name (section-heading.md, not SECTION-HEADING.md) — cosmetic " +
                         "path formatting, not a security-sensitive comparison CA1308 guards against.")]
    private static string UniqueSlug(string heading, HashSet<string> seenSlugs)
    {
        string baseSlug = NonSlugCharacters().Replace(heading.ToLowerInvariant(), "-").Trim('-');
        if (baseSlug.Length == 0)
        {
            baseSlug = "section";
        }

        string slug = baseSlug;
        int suffix = 2;
        while (!seenSlugs.Add(slug))
        {
            slug = $"{baseSlug}-{suffix++}";
        }

        return slug;
    }
}
