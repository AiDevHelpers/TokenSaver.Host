using AiDevTools.TokenSaver.Common.Core.Configuration;
using AiDevTools.TokenSaver.MCP.HealthChecks;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

using Xunit;

namespace AiDevTools.TokenSaver.MCP.Tests.Instructions;

public sealed class FolderIndexHealthCheckTests : IDisposable
{
    private readonly string _root;

    public FolderIndexHealthCheckTests()
    {
        _root = Directory.CreateTempSubdirectory("fihc-").FullName;
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        File.WriteAllText(Path.Combine(_root, "CLAUDE.md"), "# Repo\n\nSee docs/folder-index.md.\n");
        File.WriteAllText(Path.Combine(_root, "docs", "guide.md"), "# Guide\n\nSummary.\n");
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    private HealthCheckResult Run()
    {
        var config = new TokenSaverConfig { WorkspaceRoot = _root };
        var check = new FolderIndexHealthCheck(Options.Create(config));
        return check.CheckHealthAsync(new HealthCheckContext()).GetAwaiter().GetResult();
    }

    [Fact]
    public void CheckHealth_TableStale_ReportsDegraded()
    {
        File.WriteAllText(Path.Combine(_root, "docs", "folder-index.md"), "| old |\n");

        HealthCheckResult result = Run();

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("stale", result.Description ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CheckHealth_TableCurrent_ReportsHealthy()
    {
        AiDevTools.TokenSaver.MCP.Services.FolderFileTable.Generate(_root);

        HealthCheckResult result = Run();

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }
}
