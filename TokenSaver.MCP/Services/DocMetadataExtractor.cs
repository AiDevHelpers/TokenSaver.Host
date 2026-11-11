namespace TokenSaver.MCP.Services;

/// <summary>Metadata-only view of a markdown document: never carries body text.</summary>
internal sealed record DocMetadata(string Title, string Summary);

/// <summary>
///     Extracts a title and one-line summary from a markdown document. Deterministic; no model calls.
/// </summary>
internal static class DocMetadataExtractor
{
    /// <summary>Maximum summary length in characters.</summary>
    public const int MaxSummaryLength = 120;

    /// <summary>Title = frontmatter <c>name</c>, else first H1. Summary = frontmatter <c>description</c>, else first body line.</summary>
    public static DocMetadata Extract(string text)
    {
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        (string? fmName, string? fmDescription) = InstructionStoreService.ParseFrontmatter(normalized);

        string body = StripFrontmatter(normalized);
        string? h1 = null;
        string? firstLine = null;
        bool inFence = false;

        foreach (string raw in body.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence || line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                h1 ??= line[2..].Trim();
                continue;
            }

            if (line.StartsWith('#'))
            {
                continue;
            }

            firstLine = line;
            break;
        }

        string title = !string.IsNullOrWhiteSpace(fmName) ? fmName! : h1 ?? string.Empty;
        string summary = !string.IsNullOrWhiteSpace(fmDescription) ? fmDescription! : firstLine ?? string.Empty;
        return new DocMetadata(title, Truncate(summary));
    }

    private static string StripFrontmatter(string normalized)
    {
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            return normalized;
        }

        int end = normalized.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            return normalized;
        }

        int next = normalized.IndexOf('\n', end + 4);
        return next < 0 ? string.Empty : normalized[(next + 1)..];
    }

    private static string Truncate(string value)
    {
        if (value.Length <= MaxSummaryLength)
        {
            return value;
        }

        return string.Concat(value.AsSpan(0, MaxSummaryLength - 3), "...");
    }
}
