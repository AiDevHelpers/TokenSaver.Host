using TokenSaver.MCP.Services;
using Xunit;

namespace TokenSaver.MCP.Tests.Services;

public sealed class VsInstanceRegistryTests
{
    [Fact]
    public void Manifest_WithLogRoots_Parsed()
    {
        const string json = """
            {
              "pid": 4321,
              "pipeName": "TokenSaver.VSCode.4321",
              "solutionPath": "C:\\work\\app",
              "updatedUtc": "2026-11-15T12:00:00Z",
              "ide": "VsCode",
              "logRoots": ["C:\\Users\\me\\AppData\\Roaming\\Code\\logs\\20261115T1\\window1\\exthost"]
            }
            """;

        var instance = VsInstanceRegistry.Parse(json);

        Assert.NotNull(instance);
        Assert.Equal("VsCode", instance!.Ide);
        Assert.Equal(new[] { @"C:\Users\me\AppData\Roaming\Code\logs\20261115T1\window1\exthost" }, instance.LogRoots);
    }

    [Fact]
    public void Manifest_WithoutLogRoots_StillLoads()
    {
        const string json = """
            {
              "pid": 4321,
              "pipeName": "TokenSaver.VS.4321",
              "solutionPath": "C:\\work\\app.sln",
              "updatedUtc": "2026-11-15T12:00:00Z"
            }
            """;

        var instance = VsInstanceRegistry.Parse(json);

        Assert.NotNull(instance);
        Assert.Equal("VisualStudio", instance!.Ide);
        Assert.Empty(instance.LogRoots);
    }

    [Fact]
    public void Manifest_Malformed_ReturnsNull()
    {
        Assert.Null(VsInstanceRegistry.Parse("{ not json"));
    }
}
