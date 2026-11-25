using AiDevTools.TokenSaver.MCP.Services;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AiDevTools.TokenSaver.MCP.Tests;

public sealed class InstructionAutoSplitterTests
{
    private const string DocPath = @"C:\fake\CLAUDE.md";

    private static string Repeat(string text, int times) => string.Concat(Enumerable.Repeat(text, times));

    [Fact]
    public void Split_ShortDoc_ReturnsUnchangedWithNoSections()
    {
        string content = "# CLAUDE.md\n\nShort and focused.\n";

        (string result, var sections) = InstructionAutoSplitter.Split(DocPath, content, [], NullLogger.Instance);

        Assert.Equal(content, result);
        Assert.Empty(sections);
    }

    [Fact]
    public void Split_LargeDocNoSections_ReturnsUnchanged()
    {
        string content = "# CLAUDE.md\n\n" + Repeat("Just one long paragraph with no headings at all. ", 200);

        (string result, var sections) = InstructionAutoSplitter.Split(DocPath, content, [], NullLogger.Instance);

        Assert.Equal(content, result);
        Assert.Empty(sections);
    }

    [Fact]
    public void Split_OversizedDocWithLargeSection_ShrinksContentAndReturnsSubDoc()
    {
        string bigSectionBody = Repeat("This is detailed guidance that only matters sometimes. ", 40);
        string content =
            "# CLAUDE.md\n\n" +
            "Short intro that always matters.\n\n" +
            "## Detailed Guidance\n" +
            bigSectionBody + "\n\n" +
            Repeat("padding to cross the doc size threshold. ", 60);

        (string result, var sections) = InstructionAutoSplitter.Split(DocPath, content, [], NullLogger.Instance);

        // The in-memory content shrank and now contains a pointer instead of the full section.
        Assert.Contains("loaded on demand", result, StringComparison.Ordinal);
        Assert.DoesNotContain(bigSectionBody, result, StringComparison.Ordinal);
        Assert.True(result.Length < content.Length);
        Assert.Contains("Short intro that always matters.", result, StringComparison.Ordinal);

        DiscoveredSubDoc sub = Assert.Single(sections);
        Assert.Equal("detailed-guidance", sub.Slug);
        Assert.Contains(bigSectionBody.Trim(), sub.BodyContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Split_OnlySmallSections_LeavesContentUntouchedDespiteOverallSize()
    {
        // Many small sections that together cross the doc-size threshold, but no single section
        // is individually large enough to be worth extracting.
        string section = "## Small Topic {0}\nA couple of short sentences about this topic.\n\n";
        string content = "# CLAUDE.md\n\n" + string.Concat(Enumerable.Range(1, 80).Select(i => string.Format(null, section, i)));
        Assert.True(content.Length > 4000, "test setup should exceed the doc-size threshold");

        (string result, var sections) = InstructionAutoSplitter.Split(DocPath, content, [], NullLogger.Instance);

        Assert.Equal(content, result);
        Assert.Empty(sections);
    }

    [Fact]
    public void Split_SlugCollisionWithSharedSet_GetsDisambiguated()
    {
        string bigSectionBody = Repeat("Detail. ", 600);
        string content = "# CLAUDE.md\n\nIntro.\n\n## Setup\n" + bigSectionBody + "\n\n" + Repeat("padding text here. ", 60);

        HashSet<string> seenSlugs = new(StringComparer.OrdinalIgnoreCase) { "setup" };
        (_, var sections) = InstructionAutoSplitter.Split(DocPath, content, seenSlugs, NullLogger.Instance);

        DiscoveredSubDoc sub = Assert.Single(sections);
        Assert.Equal("setup-2", sub.Slug);
    }
}
