using TokenSaver.Common.Core.Configuration;
using TokenSaver.Library.Services;
using TokenSaver.MCP.Services;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using TokenSaver.OllamaIntegration.Services;


namespace TokenSaver.MCP.HealthChecks;

/// <summary>
///     Exercises a handful of the server's own lightweight, in-process code paths (no network
///     I/O — this deliberately does not call DraftModel/Ollama; that's <see cref="DraftModelHealthCheck" />'s
///     job) to catch a broken configuration/wiring bug before a real caller does. Mirrors
///     DraftModel.Loader's <c>RunSelfTests</c> in RouterHttpServer: each check is independent —
///     one throwing doesn't stop the rest — and a failure is reported as Degraded with which
///     check failed and why, even though the process is obviously still up and responding.
///     Deliberately untagged so it participates in <c>/health</c> but not the
///     <c>"ready"</c>-tagged <c>/health/ready</c> predicate — this is a self-diagnostic, not a
///     dependency-reachability check.
/// </summary>
internal sealed class SelfTestHealthCheck(
    IOptions<TokenSaverConfig> config,
    OllamaEndpointRegistry endpointRegistry) : IHealthCheck
{
    /// <summary>
    ///     Extra checks appended to the built-in suite. Empty in production; tests use this to
    ///     inject a deliberately-failing check without needing to break real config/wiring, to
    ///     verify the degraded-status wiring end to end.
    /// </summary>
    internal List<(string Name, Func<bool> Check)> ExtraChecks { get; } = [];

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        List<(string Name, Func<bool> Check)> checks =
        [
            ("config-bound", () => !string.IsNullOrWhiteSpace(config.Value.OllamaUrl)),
            ("workspace-root-resolvable", () =>
                string.IsNullOrWhiteSpace(config.Value.WorkspaceRoot) ||
                Directory.Exists(config.Value.WorkspaceRoot)),
            ("endpoint-registry-has-endpoint", () => endpointRegistry.GetAllEndpoints().Count > 0),
            .. ExtraChecks
        ];

        List<object> results = new(checks.Count);
        bool allOk = true;
        foreach ((string name, Func<bool> check) in checks)
        {
            try
            {
                bool ok = check();
                results.Add(new { name, ok, error = ok ? null : "check returned false" });
                allOk &= ok;
            }
            catch (Exception ex)
            {
                results.Add(new { name, ok = false, error = ex.Message });
                allOk = false;
            }
        }

        Dictionary<string, object> data = new() { ["checks"] = results };

        HealthCheckResult result = allOk
            ? HealthCheckResult.Healthy("Self-test checks passed.", data)
            : HealthCheckResult.Degraded("One or more self-test checks failed.", data: data);

        return Task.FromResult(result);
    }
}
