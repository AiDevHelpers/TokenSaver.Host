using AiDevTools.TokenSaver.MCP.Services;

using Xunit;

namespace AiDevTools.TokenSaver.MCP.Tests.Instructions;

public sealed class FolderFileTableTests : IDisposable
{
    private readonly string _root;

    public FolderFileTableTests()
    {
        _root = Directory.CreateTempSubdirectory("ffti-").FullName;
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Render_Rows_ProducesHeaderAndSortedRows()
    {
        FolderFileRow[] rows =
        [
            new("docs/b.md", "file", "B", "Second doc.", "code:docs/b.md", null),
            new("docs/a.md", "file", "A", "First doc.", "code:docs/a.md", null),
        ];

        string markdown = FolderFileTable.Render(rows);

        string[] lines = markdown.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("| Path | Kind | Title | Summary | Code | Semantic |", lines[0]);
        Assert.StartsWith("| docs/a.md |", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("| docs/b.md |", lines[3], StringComparison.Ordinal);
    }

    [Fact]
    public void Render_MissingSemanticKey_ShowsPending()
    {
        FolderFileRow[] rows = [new("docs/a.md", "file", "A", "Doc.", "code:docs/a.md", null)];

        string markdown = FolderFileTable.Render(rows);

        Assert.Contains("| sem:pending |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_PipeAndNewlineInSummary_AreEscaped()
    {
        FolderFileRow[] rows = [new("docs/a.md", "file", "A", "left | right\nnext", "code:docs/a.md", null)];

        string markdown = FolderFileTable.Render(rows);

        Assert.Contains("left \\| right next", markdown, StringComparison.Ordinal);
        Assert.Equal(3, markdown.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void Render_SameInput_IsDeterministic()
    {
        FolderFileRow[] rows = [new("docs/a.md", "file", "A", "Doc.", "code:docs/a.md", "sem:docs/a.md#x")];

        Assert.Equal(FolderFileTable.Render(rows), FolderFileTable.Render(rows));
    }

    [Fact]
    public void Collect_IncludesDocsAndInstructionFiles_NotCodeOrArchive()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        Directory.CreateDirectory(Path.Combine(_root, "archive"));
        Directory.CreateDirectory(Path.Combine(_root, "instructions", "skills", "tdd"));
        Directory.CreateDirectory(Path.Combine(_root, ".claude", "rules"));
        File.WriteAllText(Path.Combine(_root, "docs", "guide.md"), "# Guide\n\nHow to work.\n");
        File.WriteAllText(Path.Combine(_root, "archive", "old.md"), "# Old\n\nIgnore me.\n");
        File.WriteAllText(Path.Combine(_root, "instructions", "skills", "tdd", "SKILL.md"),
            "---\nname: tdd\ndescription: Test first.\n---\n# TDD\n");
        File.WriteAllText(Path.Combine(_root, "CLAUDE.md"), "# Repo\n\nRules here.\n");
        File.WriteAllText(Path.Combine(_root, "src.cs"), "class X {}");

        IReadOnlyList<FolderFileRow> rows = FolderFileTable.Collect(_root);

        Assert.Contains(rows, r => r.Path == "docs/guide.md" && r.Title == "Guide" && r.Summary == "How to work.");
        Assert.Contains(rows, r => r.Path == "instructions/skills/tdd/SKILL.md" && r.Title == "tdd" && r.Summary == "Test first.");
        Assert.Contains(rows, r => r.Path == "CLAUDE.md" && r.Title == "Repo");
        Assert.DoesNotContain(rows, r => r.Path.StartsWith("archive/", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, r => r.Path == "src.cs");
    }

    [Fact]
    public void WriteIfChanged_NewFile_WritesAndReturnsTrue()
    {
        string path = Path.Combine(_root, "docs", "folder-index.md");

        bool written = FolderFileTable.WriteIfChanged(path, "table\n");

        Assert.True(written);
        Assert.Equal("table\n", File.ReadAllText(path));
    }

    [Fact]
    public void WriteIfChanged_SameContent_DoesNotRewrite()
    {
        string path = Path.Combine(_root, "folder-index.md");
        File.WriteAllText(path, "table\n");
        DateTime before = File.GetLastWriteTimeUtc(path);

        bool written = FolderFileTable.WriteIfChanged(path, "table\n");

        Assert.False(written);
        Assert.Equal(before, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void Generate_WritesDocsFolderIndex_AndIsIdempotent()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        File.WriteAllText(Path.Combine(_root, "docs", "guide.md"), "# Guide\n\nSummary.\n");

        (string path, bool changed) first = FolderFileTable.Generate(_root);
        (string path, bool changed) second = FolderFileTable.Generate(_root);

        Assert.Equal(Path.Combine(_root, "docs", "folder-index.md"), first.path);
        Assert.True(first.changed);
        Assert.False(second.changed);
        Assert.Contains("| docs/guide.md | file | Guide | Summary. |", File.ReadAllText(first.path), StringComparison.Ordinal);
    }

    [Fact]
    public void CheckSync_TableMissing_ReportsMissing()
    {
        File.WriteAllText(Path.Combine(_root, "CLAUDE.md"), "# Repo\n\nSee docs/folder-index.md.\n");

        IReadOnlyList<string> problems = FolderFileTable.CheckSync(_root);

        Assert.Contains(problems, p => p.Contains("missing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CheckSync_TableStale_ReportsStale()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        File.WriteAllText(Path.Combine(_root, "CLAUDE.md"), "# Repo\n\nSee docs/folder-index.md.\n");
        File.WriteAllText(Path.Combine(_root, "docs", "guide.md"), "# Guide\n\nSummary.\n");
        File.WriteAllText(Path.Combine(_root, "docs", "folder-index.md"), "| old |\n");

        IReadOnlyList<string> problems = FolderFileTable.CheckSync(_root);

        Assert.Contains(problems, p => p.Contains("stale", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CheckSync_TableCurrentAndLinked_ReturnsNoProblems()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        File.WriteAllText(Path.Combine(_root, "CLAUDE.md"), "# Repo\n\nSee docs/folder-index.md.\n");
        File.WriteAllText(Path.Combine(_root, "docs", "guide.md"), "# Guide\n\nSummary.\n");
        FolderFileTable.Generate(_root);

        Assert.Empty(FolderFileTable.CheckSync(_root));
    }

    [Fact]
    public void CheckSync_ClaudeMdNotLinkedToTable_ReportsUnlinked()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        File.WriteAllText(Path.Combine(_root, "CLAUDE.md"), "# Repo\n\nNo pointer here.\n");
        FolderFileTable.Generate(_root);

        IReadOnlyList<string> problems = FolderFileTable.CheckSync(_root);

        Assert.Contains(problems, p => p.Contains("CLAUDE.md", StringComparison.Ordinal) && p.Contains("link", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StatusLine_NoDocsFolder_ReturnsNull()
    {
        Assert.Null(FolderFileTable.StatusLine(_root));
    }

    [Fact]
    public void StatusLine_TableStale_ReportsStale()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        File.WriteAllText(Path.Combine(_root, "CLAUDE.md"), "See docs/folder-index.md.\n");
        File.WriteAllText(Path.Combine(_root, "docs", "folder-index.md"), "| old |\n");

        string? line = FolderFileTable.StatusLine(_root);

        Assert.NotNull(line);
        Assert.Contains("stale", line!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StatusLine_InSync_ReportsInSync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        File.WriteAllText(Path.Combine(_root, "CLAUDE.md"), "See docs/folder-index.md.\n");
        File.WriteAllText(Path.Combine(_root, "docs", "guide.md"), "# Guide\n\nSummary.\n");
        FolderFileTable.Generate(_root);

        Assert.Equal("folder-index: in sync", FolderFileTable.StatusLine(_root));
    }

    [Fact]
    public void Collect_Rows_CarryCodeLinkAndNoBody()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        File.WriteAllText(Path.Combine(_root, "docs", "guide.md"), "# Guide\n\nSummary.\n\nBODY-MARKER\n");

        FolderFileRow row = Assert.Single(FolderFileTable.Collect(_root), r => r.Path == "docs/guide.md");

        Assert.Equal("code:docs/guide.md", row.CodeKey);
        Assert.Null(row.SemanticKey);
        Assert.DoesNotContain("BODY-MARKER", row.Summary, StringComparison.Ordinal);
    }
}
