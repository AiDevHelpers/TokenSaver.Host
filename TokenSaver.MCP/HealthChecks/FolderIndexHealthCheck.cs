using TokenSaver.Common.Core.Configuration;
using TokenSaver.MCP.Services;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace TokenSaver.MCP.HealthChecks;

/// <summary>
///     Reports Degraded when the generated folder/file table (docs/folder-index.md) is missing,
///     stale, or not linked from CLAUDE.md. Read-only; untagged so it appears in <c>/health</c> only.
/// </summary>
internal sealed class FolderIndexHealthCheck(IOptions<TokenSaverConfig> config) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        string root = config.Value.WorkspaceRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return Task.FromResult(HealthCheckResult.Healthy("workspace root not set; folder index skipped"));
        }

        IReadOnlyList<string> problems = FolderFileTable.CheckSync(root);
        return Task.FromResult(problems.Count == 0
            ? HealthCheckResult.Healthy("folder index in sync")
            : HealthCheckResult.Degraded(string.Join(" ", problems)));
    }
}
