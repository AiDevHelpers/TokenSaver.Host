using AiDevTools.TokenSaver.MCP.Services;
using AiDevTools.TokenSaver.MCP.Tools;

using Xunit;

namespace AiDevTools.TokenSaver.MCP.Tests.Tools;

/// <summary>
///     Covers InstructionTools against a temp instruction store with a single SKILL.md, so the
///     user-profile instruction folder is never read or written.
/// </summary>
public sealed class InstructionToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tokensaver-instructions-tests-" + Guid.NewGuid().ToString("N"));

    public InstructionToolsTests()
    {
        string skillDir = Path.Combine(_root, "tdd");
        _ = Directory.CreateDirectory(skillDir);
        File.WriteAllText(
            Path.Combine(skillDir, InstructionStoreService.SkillFileName),
            "---\nname: tdd\ndescription: Test first workflow\n---\n# TDD body\n");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void ListInstructions_StoreHasSkill_ListsNameAndDescription()
    {
        InstructionTools tools = new(new InstructionStoreService(_root));

        string result = tools.ListInstructions();

        Assert.Equal("- tdd: Test first workflow", result);
    }

    [Fact]
    public void ListInstructions_EmptyStore_ReturnsNoInstructionsMessage()
    {
        string empty = Path.Combine(_root, "does-not-exist");
        InstructionTools tools = new(new InstructionStoreService(empty));

        string result = tools.ListInstructions();

        Assert.StartsWith("No instructions found in", result, StringComparison.Ordinal);
        Assert.Contains(InstructionStoreService.EnvVarName, result, StringComparison.Ordinal);
    }

    [Fact]
    public void GetInstruction_KnownName_ReturnsFullText()
    {
        InstructionTools tools = new(new InstructionStoreService(_root));

        string result = tools.GetInstruction("tdd");

        Assert.Contains("# TDD body", result, StringComparison.Ordinal);
    }

    [Fact]
    public void GetInstruction_UnknownName_ReturnsNotFoundMessage()
    {
        InstructionTools tools = new(new InstructionStoreService(_root));

        string result = tools.GetInstruction("nope");

        Assert.Equal("No instruction named 'nope'. Call list_instructions to see available names.", result);
    }

    [Fact]
    public void GetInstruction_TraversalName_IsRejectedAsNotFound()
    {
        InstructionTools tools = new(new InstructionStoreService(_root));

        string result = tools.GetInstruction("../escape");

        Assert.StartsWith("No instruction named '../escape'", result, StringComparison.Ordinal);
    }
}
