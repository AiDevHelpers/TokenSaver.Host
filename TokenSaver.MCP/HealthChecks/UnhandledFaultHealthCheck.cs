using TokenSaver.MCP.Services;

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TokenSaver.MCP.HealthChecks;

/// <summary>
///     Reports Degraded when <see cref="UnhandledFaultTracker" /> has a recent unhandled
///     exception on record (see <see cref="Middleware.FaultTrackingMiddleware" />), even though
///     the process itself is clearly still up and running (it's answering this very health
///     check). Deliberately untagged — it participates in the full <c>/health</c> report but not
///     the <c>"ready"</c>-tagged predicate <c>/health/ready</c> uses, matching
///     DraftModel.Loader's split between full self-diagnostic health and cheap readiness.
/// </summary>
internal sealed class UnhandledFaultHealthCheck(UnhandledFaultTracker faultTracker) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        string? faultMessage = faultTracker.LastFaultMessage;

        HealthCheckResult result = faultMessage is null
            ? HealthCheckResult.Healthy("No unhandled exceptions recorded recently.")
            : HealthCheckResult.Degraded(
                $"An unhandled exception occurred recently: {faultMessage}",
                data: new Dictionary<string, object> { ["lastError"] = faultMessage });

        return Task.FromResult(result);
    }
}
