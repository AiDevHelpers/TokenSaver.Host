using TokenSaver.MCP.Services;

using Xunit;

namespace TokenSaver.MCP.Tests.Instructions;

public sealed class GlobalInstructionCheckTests : IDisposable
{
    private readonly string _base;
    private readonly string _source;
    private readonly string _installed;
    private readonly string _store;

    public GlobalInstructionCheckTests()
    {
        _base = Directory.CreateTempSubdirectory("gic-").FullName;
        _source = Path.Combine(_base, "claude_rules");
        _installed = Path.Combine(_base, "dotclaude");
        _store = Path.Combine(_base, "store");
        Directory.CreateDirectory(Path.Combine(_source, "rules"));
        Directory.CreateDirectory(Path.Combine(_installed, "rules"));
        Directory.CreateDirectory(_store);
    }

    public void Dispose()
    {
        Directory.Delete(_base, recursive: true);
    }

    private void AddStoreSkill(string name, string text)
    {
        Directory.CreateDirectory(Path.Combine(_store, name));
        File.WriteAllText(Path.Combine(_store, name, "SKILL.md"), text);
    }

    private void AddInstalledStub(string name, string text)
    {
        Directory.CreateDirectory(Path.Combine(_installed, "skills", name));
        File.WriteAllText(Path.Combine(_installed, "skills", name, "SKILL.md"), text);
    }

    [Fact]
    public void Check_RuleDiffersFromSource_ReportsDrift()
    {
        File.WriteAllText(Path.Combine(_source, "rules", "writing.md"), "new\n");
        File.WriteAllText(Path.Combine(_installed, "rules", "writing.md"), "old\n");

        IReadOnlyList<string> problems = GlobalInstructionCheck.Check(_source, _installed, _store);

        Assert.Contains(problems, p => p.Contains("writing.md", StringComparison.Ordinal) && p.Contains("differs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Check_RuleMissingFromInstalled_ReportsMissing()
    {
        File.WriteAllText(Path.Combine(_source, "rules", "writing.md"), "new\n");

        IReadOnlyList<string> problems = GlobalInstructionCheck.Check(_source, _installed, _store);

        Assert.Contains(problems, p => p.Contains("writing.md", StringComparison.Ordinal) && p.Contains("missing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Check_StoreSkillNameMismatchesFolder_ReportsNameMismatch()
    {
        AddStoreSkill("tdd", "---\nname: other\ndescription: x\n---\n# TDD\n");

        IReadOnlyList<string> problems = GlobalInstructionCheck.Check(_source, _installed, _store);

        Assert.Contains(problems, p => p.Contains("tdd", StringComparison.Ordinal) && p.Contains("name", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Check_InstalledStubDoesNotPointAtStore_ReportsStub()
    {
        AddInstalledStub("tdd", "# old copied body, no pointer\n");

        IReadOnlyList<string> problems = GlobalInstructionCheck.Check(_source, _installed, _store);

        Assert.Contains(problems, p => p.Contains("tdd", StringComparison.Ordinal) && p.Contains("get_instruction", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_AllLayersAgree_ReturnsNoProblems()
    {
        File.WriteAllText(Path.Combine(_source, "rules", "writing.md"), "same\n");
        File.WriteAllText(Path.Combine(_installed, "rules", "writing.md"), "same\n");
        AddStoreSkill("tdd", "---\nname: tdd\ndescription: x\n---\n# TDD\n");
        AddInstalledStub("tdd", "Call get_instruction with name tdd.\n");

        Assert.Empty(GlobalInstructionCheck.Check(_source, _installed, _store));
    }
}
