using TokenSaver.MCP.Services;

using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Globalization;
using System.Text;

namespace TokenSaver.MCP.Tools;

[McpServerToolType]
internal sealed class InstructionTools(InstructionStoreService store)
{
    [McpServerTool]
    [Description(
        "List the editable instruction store: the name and one-line description of each skill. " +
        "Call GetInstruction with a name to read the full text.")]
    public string ListInstructions()
    {
        IReadOnlyList<InstructionInfo> items = store.List();
        if (items.Count == 0)
        {
            return $"No instructions found in {InstructionStoreService.DefaultRoot}. " +
                   $"Set {InstructionStoreService.EnvVarName} or run the installer to populate it.";
        }

        StringBuilder sb = new();
        foreach (InstructionInfo item in items)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"- {item.Name}: {item.Description}");
        }

        return sb.ToString().TrimEnd();
    }

    [McpServerTool]
    [Description("Return the full text of one instruction from the store. Use ListInstructions to find names.")]
    public string GetInstruction(
        [Description("Instruction name from ListInstructions, e.g. 'tdd' or 'implement-plan'")] string name)
    {
        string? text = store.Get(name);
        return text ?? $"No instruction named '{name}'. Call ListInstructions to see available names.";
    }
}
