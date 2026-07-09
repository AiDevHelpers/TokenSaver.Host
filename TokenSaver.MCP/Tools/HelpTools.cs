using TokenSaver.MCP.Services;

using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Globalization;
using System.Text;

namespace TokenSaver.MCP.Tools;

[McpServerToolType]
internal sealed class HelpTools
{
    private readonly McpContextService _mcpContext;

    public HelpTools(McpContextService mcpContext)
    {
        _mcpContext = mcpContext;
    }

    [McpServerTool]
    [Description(
        "List all available TokenSaver MCP tools, optionally filtered by category or tool name. " +
        "Categories: Context, Code, Auto-Workflow, Diagnostics, Setup. " +
        "Call with no arguments for a full overview. Pass a category name or tool name to narrow results.")]
    public string Help(
        [Description("Optional category name or tool name to filter results (case-insensitive)")]
        string? topic = null)
    {
        McpContextService.ToolEntry[] registry = McpContextService.Registry;

        // Filter if topic was given
        IEnumerable<McpContextService.ToolEntry> entries = registry.Where(e => e.ShowInHelp);
        if (!string.IsNullOrWhiteSpace(topic))
        {
            string t = topic.Trim();
            entries = registry.Where(e =>
                e.Category.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                e.Name.Contains(t, StringComparison.OrdinalIgnoreCase));

            if (!entries.Any())
            {
                return $"No tools or categories matching '{t}'.\n\n" +
                       $"Available categories: {string.Join(", ", registry.Select(e => e.Category).Distinct())}";
            }
        }

        List<IGrouping<string, McpContextService.ToolEntry>> groups = entries
            .GroupBy(e => e.Category)
            .OrderBy(g => g.Key)
            .ToList();

        StringBuilder sb = new();

        if (string.IsNullOrWhiteSpace(topic))
        {
            _ = sb.AppendLine("# TokenSaver MCP — Tool Reference");
            _ = sb.AppendLine();
            _ = sb.AppendLine("Use `Help(topic: \"<category or tool name>\")` to filter.");
            _ = sb.AppendLine();
        }

        foreach (IGrouping<string, McpContextService.ToolEntry>? group in groups)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"## {group.Key}");
            _ = sb.AppendLine();
            foreach (McpContextService.ToolEntry? e in group)
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"### {e.Name}");
                _ = sb.AppendLine(e.Description);
                _ = sb.AppendLine();
            }
        }

        return sb.ToString().TrimEnd();
    }
}
