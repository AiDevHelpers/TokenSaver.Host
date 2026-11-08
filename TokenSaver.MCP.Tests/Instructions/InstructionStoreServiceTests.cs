using TokenSaver.MCP.Services;

using Xunit;

namespace TokenSaver.MCP.Tests.Instructions;

public sealed class InstructionStoreServiceTests : IDisposable
{
    private readonly string _root;

    public InstructionStoreServiceTests()
    {
        _root = Directory.CreateTempSubdirectory("istore-tests-").FullName;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void WriteSkill(string folder, string content)
    {
        string dir = Path.Combine(_root, folder);
        _ = Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), content);
    }

    [Fact]
    public void List_ReturnsNameAndDescriptionFromFrontmatter_SortedByName()
    {
        WriteSkill("tdd", "---\nname: tdd\ndescription: \"Test first.\"\n---\nBody A");
        WriteSkill("lint", "---\nname: lint\ndescription: Lint stance.\n---\nBody B");

        IReadOnlyList<InstructionInfo> items = new InstructionStoreService(_root).List();

        Assert.Equal(["lint", "tdd"], items.Select(i => i.Name));
        Assert.Equal("Lint stance.", items[0].Description);
        Assert.Equal("Test first.", items[1].Description);
    }

    [Fact]
    public void List_FolderWithoutSkillFile_IsIgnored()
    {
        _ = Directory.CreateDirectory(Path.Combine(_root, "empty"));

        Assert.Empty(new InstructionStoreService(_root).List());
    }

    [Fact]
    public void List_MissingRoot_ReturnsEmpty()
    {
        string missing = Path.Combine(_root, "does-not-exist");

        Assert.Empty(new InstructionStoreService(missing).List());
    }

    [Fact]
    public void Get_ReturnsFullTextIncludingFrontmatter()
    {
        const string text = "---\nname: tdd\ndescription: Test first.\n---\nBody";
        WriteSkill("tdd", text);

        Assert.Equal(text, new InstructionStoreService(_root).Get("tdd"));
    }

    [Fact]
    public void Get_UnknownName_ReturnsNull()
    {
        Assert.Null(new InstructionStoreService(_root).Get("nope"));
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData("Upper")]
    public void Get_InvalidName_ReturnsNullAndNeverReadsOutsideRoot(string name)
    {
        Assert.Null(new InstructionStoreService(_root).Get(name));
    }

    [Fact]
    public void ParseFrontmatter_NoFrontmatter_ReturnsNulls()
    {
        (string? name, string? description) = InstructionStoreService.ParseFrontmatter("# just a heading");

        Assert.Null(name);
        Assert.Null(description);
    }

    [Fact]
    public void SelectRelevantSections_MatchingPrompt_ReturnsTaggedHeadingSection()
    {
        WriteSkill("tdd", "---\nname: tdd\ndescription: Test first.\n---\n" +
                          "# TDD\n\nIntro text.\n\n## Debugging failures\n\nWhen debugging, reproduce the failing test first.\n\n" +
                          "## Unrelated\n\nNothing here about gardening.");

        string result = new InstructionStoreService(_root).SelectRelevantSections("I am debugging a failing test");

        Assert.Contains("<!-- instruction: tdd > Debugging failures -->", result, StringComparison.Ordinal);
        Assert.Contains("reproduce the failing test first", result, StringComparison.Ordinal);
        Assert.DoesNotContain("gardening", result, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectRelevantSections_UnrelatedPrompt_ReturnsEmpty()
    {
        WriteSkill("tdd", "---\nname: tdd\ndescription: Test first.\n---\n## Debugging\n\nReproduce the failing test.");

        Assert.Equal("", new InstructionStoreService(_root).SelectRelevantSections("bake a chocolate cake"));
    }

    [Fact]
    public void SelectRelevantSections_RespectsMaxChars()
    {
        WriteSkill("tdd", "---\nname: tdd\ndescription: Test first.\n---\n## Testing\n\n" + new string('x', 200) + " test");

        string result = new InstructionStoreService(_root).SelectRelevantSections("write a test", maxChars: 50);

        Assert.Equal("", result);
    }

    [Fact]
    public void PromptTerms_StemsPluralsAndDropsStopwordsAndShortWords()
    {
        HashSet<string> terms = InstructionStoreService.PromptTerms("Please fix the failing tests with this code");

        Assert.Contains("test", terms);
        Assert.DoesNotContain("fix", terms);
        Assert.DoesNotContain("this", terms);
        Assert.DoesNotContain("with", terms);
    }
}
