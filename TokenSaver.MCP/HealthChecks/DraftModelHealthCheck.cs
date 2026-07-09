using TokenSaver.Library.Models.Ollama;
using TokenSaver.Library.Services;

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TokenSaver.MCP.HealthChecks;

/// <summary>
///     Health check that probes the DraftModel router (via OllamaService) and reports
///     whether it is reachable, has models available, and has models loaded in VRAM.
///     Healthy   — router reachable, ≥1 model available (VRAM state reported via data["vram_state"];
///                 "idle" just means keep_alive expired the model, not a problem — next request
///                 reloads it, incurring a one-time cold-load delay)
///     Unhealthy — router unreachable, or no models pulled
/// </summary>
internal sealed class DraftModelHealthCheck(OllamaService ollama) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        OllamaStatusResult status;
        try
        {
            status = await ollama.GetOllamaStatusAsync();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(
                "DraftModel router threw an exception during health probe.",
                ex);
        }

        if (!status.Running)
        {
            return HealthCheckResult.Unhealthy(
                "DraftModel router is not reachable. " +
                "Ensure DraftModel.Loader is running (dotnet run --project DraftModel/DraftModel.Loader -- --service).");
        }

        if (status.Models.Length == 0)
        {
            return HealthCheckResult.Unhealthy(
                "DraftModel router is reachable but no models are pulled. " +
                "Run DraftModel.Loader with --service to pull required models.");
        }

        bool idle = status.LoadedInVram.Length == 0;
        Dictionary<string, object> data = new()
        {
            ["models_available"] = status.Models,
            ["models_in_vram"] = status.LoadedInVram,
            ["vram_state"] = idle ? "idle" : "loaded"
        };

        return idle
            ? HealthCheckResult.Healthy(
                $"DraftModel router is up with {status.Models.Length} model(s) available, " +
                "but idle — none currently loaded in VRAM (keep_alive expired). " +
                "Next request will incur a one-time cold-load delay.",
                data)
            : HealthCheckResult.Healthy(
                $"DraftModel router is healthy. " +
                $"{status.LoadedInVram.Length}/{status.Models.Length} model(s) loaded in VRAM.",
                data);
    }
}
