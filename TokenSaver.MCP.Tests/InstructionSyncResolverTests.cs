using TokenSaver.MCP.Services;

using Xunit;

namespace TokenSaver.MCP.Tests;

public sealed class InstructionSyncResolverTests
{
    [Theory]
    [InlineData("claude-code")]
    [InlineData("claude-desktop")]
    [InlineData("cowork")]
    public void Resolve_ClaudeFamily_SkipsBecauseNativelyLoaded(string clientName)
    {
        InstructionSyncTarget target = InstructionSyncResolver.Resolve(clientName);

        Assert.False(target.ShouldSync);
        Assert.Null(target.RelativeFilePath);
    }

    [Theory]
    [InlineData("cursor")]
    [InlineData("cursor-ide")]
    public void Resolve_Cursor_TargetsScopedRulesDir(string clientName)
    {
        InstructionSyncTarget target = InstructionSyncResolver.Resolve(clientName);

        Assert.True(target.ShouldSync);
        Assert.Contains(".cursor", target.RelativeFilePath, StringComparison.Ordinal);
        Assert.Contains("alwaysApply", target.FrontMatter, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("github-copilot")]
    [InlineData("copilot-chat")]
    public void Resolve_Copilot_TargetsScopedInstructionsDir(string clientName)
    {
        InstructionSyncTarget target = InstructionSyncResolver.Resolve(clientName);

        Assert.True(target.ShouldSync);
        Assert.Contains(".github", target.RelativeFilePath, StringComparison.Ordinal);
        Assert.Contains("applyTo", target.FrontMatter, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("continue")]
    [InlineData("continue.dev")]
    public void Resolve_Continue_TargetsScopedRulesDir(string clientName)
    {
        InstructionSyncTarget target = InstructionSyncResolver.Resolve(clientName);

        Assert.True(target.ShouldSync);
        Assert.Contains(".continue", target.RelativeFilePath, StringComparison.Ordinal);
        Assert.Contains("globs", target.FrontMatter, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_UnrecognizedClient_FallsBackToAgentsMd()
    {
        InstructionSyncTarget target = InstructionSyncResolver.Resolve("some-future-tool");

        Assert.True(target.ShouldSync);
        Assert.Equal("AGENTS.md", target.RelativeFilePath);
        Assert.Null(target.FrontMatter);
    }

    [Fact]
    public void Resolve_EmptyClientName_FallsBackToAgentsMd()
    {
        InstructionSyncTarget target = InstructionSyncResolver.Resolve("");

        Assert.True(target.ShouldSync);
        Assert.Equal("AGENTS.md", target.RelativeFilePath);
    }
}
