using AiDevTools.TokenSaver.MCP.Services;

using Xunit;

namespace AiDevTools.TokenSaver.MCP.Tests;

public sealed class InstructionSubDocParserTests : IDisposable
{
    private readonly string _dir;

    public InstructionSubDocParserTests()
    {
        _dir = Directory.CreateTempSubdirectory("subdocParserTests-").FullName;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void DiscoverSubDocs_NoMarkdownReferences_ReturnsEmpty()
    {
        string mainPath = Path.Combine(_dir, "CLAUDE.md");
        var result = InstructionSubDocParser.DiscoverSubDocs(mainPath, "Just plain text, no links.");

        Assert.Empty(result);
    }

    [Fact]
    public void DiscoverSubDocs_ReferenceToNonExistentFile_IsSkipped()
    {
        string mainPath = Path.Combine(_dir, "CLAUDE.md");
        var result = InstructionSubDocParser.DiscoverSubDocs(mainPath, "See `rules/missing.md` for details.");

        Assert.Empty(result);
    }

    [Fact]
    public void DiscoverSubDocs_RelativeReferenceToExistingFile_IsDiscoveredWithParsedFrontMatter()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "rules"));
        File.WriteAllText(
            Path.Combine(_dir, "rules", "commands.md"),
            "---\npaths:\n  - \"**/*.slnx\"\n  - \"**/*.csproj\"\n---\n\n# Commands\nRun the build.");

        string mainPath = Path.Combine(_dir, "CLAUDE.md");
        var result = InstructionSubDocParser.DiscoverSubDocs(mainPath, "See `rules/commands.md`.");

        DiscoveredSubDoc doc = Assert.Single(result);
        Assert.Equal("commands", doc.Slug);
        Assert.Equal(["**/*.slnx", "**/*.csproj"], doc.FrontMatter.PathGlobs);
        Assert.Empty(doc.FrontMatter.TaskTypes);
        Assert.Contains("Run the build.", doc.BodyContent, StringComparison.Ordinal);
        Assert.DoesNotContain("paths:", doc.BodyContent, StringComparison.Ordinal);
    }

    [Fact]
    public void DiscoverSubDocs_DuplicateReferences_OnlyDiscoveredOnce()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "rules"));
        File.WriteAllText(Path.Combine(_dir, "rules", "commands.md"), "# Commands");

        string mainPath = Path.Combine(_dir, "CLAUDE.md");
        var result = InstructionSubDocParser.DiscoverSubDocs(
            mainPath, "See `rules/commands.md` or again `rules/commands.md`.");

        Assert.Single(result);
    }

    [Fact]
    public void DiscoverSubDocs_TwoDifferentFilesWithSameStem_GetUniqueSlugs()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "a"));
        Directory.CreateDirectory(Path.Combine(_dir, "b"));
        File.WriteAllText(Path.Combine(_dir, "a", "notes.md"), "A notes");
        File.WriteAllText(Path.Combine(_dir, "b", "notes.md"), "B notes");

        string mainPath = Path.Combine(_dir, "CLAUDE.md");
        var result = InstructionSubDocParser.DiscoverSubDocs(mainPath, "See `a/notes.md` and `b/notes.md`.");

        Assert.Equal(2, result.Count);
        Assert.Equal(["notes", "notes-2"], [.. result.Select(d => d.Slug)]);
    }

    [Fact]
    public void ParseFrontMatter_NoFrontMatter_ReturnsRawAsBody()
    {
        (SubDocFrontMatter frontMatter, string body) = InstructionSubDocParser.ParseFrontMatter("# Just a doc\nNo frontmatter here.");

        Assert.Empty(frontMatter.PathGlobs);
        Assert.Empty(frontMatter.TaskTypes);
        Assert.Equal("# Just a doc\nNo frontmatter here.", body);
    }

    [Fact]
    public void ParseFrontMatter_UnterminatedFrontMatter_TreatsWholeContentAsBody()
    {
        (SubDocFrontMatter frontMatter, string body) = InstructionSubDocParser.ParseFrontMatter("---\npaths:\n  - \"x\"\nno closing marker");

        Assert.Empty(frontMatter.PathGlobs);
        Assert.Contains("no closing marker", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseFrontMatter_BothPathsAndTaskTypes_ParsesBothLists()
    {
        (SubDocFrontMatter frontMatter, string body) = InstructionSubDocParser.ParseFrontMatter(
            "---\npaths:\n  - \"**/*.cs\"\ntaskTypes:\n  - Debug\n  - Test\n---\n\nBody text.");

        Assert.Equal(["**/*.cs"], frontMatter.PathGlobs);
        Assert.Equal(["Debug", "Test"], frontMatter.TaskTypes);
        Assert.Equal("Body text.", body);
    }
}
