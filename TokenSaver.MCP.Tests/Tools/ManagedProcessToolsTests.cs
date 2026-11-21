using System.Text.Json;
using TokenSaver.MCP.Tools;
using Xunit;

namespace TokenSaver.MCP.Tests.Tools;

public sealed class ManagedProcessToolsTests
{
    private static readonly ManagedProcessTools Processes = new();

    [Fact]
    public void ProcessList_ReturnsCount()
    {
        using var doc = JsonDocument.Parse(Processes.ProcessList());
        Assert.True(doc.RootElement.GetProperty("count").GetInt32() >= 0);
    }

    [Fact]
    public void ProcessKill_UntrackedPid_Refused()
    {
        using var doc = JsonDocument.Parse(Processes.ProcessKill(999));
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }
}
