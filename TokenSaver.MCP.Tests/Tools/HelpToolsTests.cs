using TokenSaver.MCP.Services;
using TokenSaver.MCP.Tools;

using Xunit;

namespace TokenSaver.MCP.Tests.Tools;

/// <summary>
///     Covers HelpTools against the static tool registry. Help reads no services at runtime.
/// </summary>
public sealed class HelpToolsTests
{
    [Fact]
    public void Help_NoTopic_ReturnsFullReferenceHeader()
    {
        HelpTools tools = new(new McpContextService());

        string result = tools.Help();

        Assert.StartsWith("# TokenSaver MCP — Tool Reference", result, StringComparison.Ordinal);
        Assert.Contains("## ", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Help_TopicMatchingToolName_ReturnsOnlyThatTool()
    {
        HelpTools tools = new(new McpContextService());

        string result = tools.Help("Calibration");

        Assert.Contains("### Calibration", result, StringComparison.Ordinal);
        Assert.DoesNotContain("### Code Work", result, StringComparison.Ordinal);
        Assert.DoesNotContain("# TokenSaver MCP — Tool Reference", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Help_UnmatchedTopic_ListsAvailableCategories()
    {
        HelpTools tools = new(new McpContextService());

        string result = tools.Help("zzz-no-such-topic");

        Assert.StartsWith("No tools or categories matching 'zzz-no-such-topic'.", result, StringComparison.Ordinal);
        Assert.Contains("Available categories:", result, StringComparison.Ordinal);
    }
}
