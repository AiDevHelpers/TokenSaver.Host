using TokenSaver.MCP.Services;

using Xunit;

namespace TokenSaver.MCP.Tests.Instructions;

public sealed class DocMetadataExtractorTests
{
    [Fact]
    public void Extract_FrontmatterPresent_UsesNameAndDescription()
    {
        const string text = "---\nname: tdd\ndescription: Test first workflow.\n---\n# Body title\n\nBody text.\n";

        DocMetadata meta = DocMetadataExtractor.Extract(text);

        Assert.Equal("tdd", meta.Title);
        Assert.Equal("Test first workflow.", meta.Summary);
    }

    [Fact]
    public void Extract_NoFrontmatter_UsesFirstH1AndFirstBodyLine()
    {
        const string text = "# Worktree rules\n\nCreate worktrees per feature.\n\nMore detail below.\n";

        DocMetadata meta = DocMetadataExtractor.Extract(text);

        Assert.Equal("Worktree rules", meta.Title);
        Assert.Equal("Create worktrees per feature.", meta.Summary);
    }

    [Fact]
    public void Extract_HeadingsAndCodeFences_AreSkippedForSummary()
    {
        const string text = "# Title\n\n## Section\n\n```bash\ndotnet test\n```\n\nThe real summary line.\n";

        DocMetadata meta = DocMetadataExtractor.Extract(text);

        Assert.Equal("The real summary line.", meta.Summary);
    }

    [Fact]
    public void Extract_LongSummary_TruncatedToMaxLength()
    {
        string longLine = new string('a', 300);
        string text = "# T\n\n" + longLine + "\n";

        DocMetadata meta = DocMetadataExtractor.Extract(text);

        Assert.True(meta.Summary.Length <= DocMetadataExtractor.MaxSummaryLength);
        Assert.EndsWith("...", meta.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_LaterBodyParagraphs_NeverAppearInResult()
    {
        const string text = "# Title\n\nFirst line.\n\nSECRET-BODY-MARKER should not leak.\n";

        DocMetadata meta = DocMetadataExtractor.Extract(text);

        Assert.DoesNotContain("SECRET-BODY-MARKER", meta.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-BODY-MARKER", meta.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_EmptyText_ReturnsEmptyTitleAndSummary()
    {
        DocMetadata meta = DocMetadataExtractor.Extract(string.Empty);

        Assert.Equal(string.Empty, meta.Title);
        Assert.Equal(string.Empty, meta.Summary);
    }
}
