using TokenSaver.OllamaIntegration.Models.Ollama;
using TokenSaver.OllamaIntegration.Services;

using Microsoft.Extensions.Diagnostics.HealthChecks;

using System.Globalization;


namespace TokenSaver.MCP.HealthChecks;

/// <summary>
///     Health check that probes the DraftModel router (via OllamaService) and reports
///     whether it is reachable, has models available, and has models loaded in VRAM. Also reports
///     host resource stats (GPU/VRAM via <c>nvidia-smi</c>, system RAM, logical CPU core count) —
///     informational only, never affects Healthy/Unhealthy classification, since a GPU-stat probe
///     failing (no nvidia-smi, no GPU) says nothing about whether the router itself is usable.
///     Healthy   — router reachable, ≥1 model available (VRAM state reported via data["vram_state"];
///                 "idle" just means keep_alive expired the model, not a problem — next request
///                 reloads it, incurring a one-time cold-load delay)
///     Unhealthy — router unreachable, or no models pulled
/// </summary>
internal sealed class SimpleModelServerHealthCheck(OllamaService ollama) : IHealthCheck
{
    private const long BytesPerMb = 1024 * 1024;

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
        GpuStats gpu = await ollama.GetGpuStatsAsync();
        GCMemoryInfo memInfo = GC.GetGCMemoryInfo();

        // Comma-joined strings, not raw string[] — the array form logs as the useless
        // "System.String[]" wherever something naively ToString()s a Data value (e.g. the
        // framework's own DefaultHealthCheckService debug-level "Health check data for X:" dump),
        // and a joined string is just as usable in the /health JSON response.
        //
        // "models_available" mirrors the router's /api/tags response, which is deliberately a
        // single virtual model name (see DraftModel's docs/router-contract.md — "single virtual
        // model" by design, not a bug): the router hides which concrete draft/main/deep/large
        // model is actually behind it. The real concrete model names only ever show up in
        // "models_in_vram" (proxied verbatim from Ollama's own /api/ps).
        Dictionary<string, object> data = new()
        {
            ["models_available"] = string.Join(", ", status.Models),
            ["models_in_vram"] = string.Join(", ", status.LoadedInVram),
            ["vram_state"] = idle ? "idle" : "loaded",
            ["gpu"] = gpu.Gpu,
            ["vram_used_mb"] = gpu.VramUsedMb?.ToString(CultureInfo.InvariantCulture) ?? "unknown",
            ["vram_total_mb"] = gpu.VramTotalMb?.ToString(CultureInfo.InvariantCulture) ?? "unknown",
            ["gpu_util_pct"] = gpu.GpuUtilPct?.ToString(CultureInfo.InvariantCulture) ?? "unknown",
            // System-wide (not per-process) physical RAM — GCMemoryInfo reports the whole
            // machine's memory load/limit, not just this process's working set.
            ["ram_used_mb"] = (memInfo.MemoryLoadBytes / BytesPerMb).ToString(CultureInfo.InvariantCulture),
            ["ram_total_mb"] = (memInfo.TotalAvailableMemoryBytes / BytesPerMb).ToString(CultureInfo.InvariantCulture),
            // Logical core count only — live host-wide CPU utilization % has no simple, reliable
            // cross-platform BCL API (would need P/Invoke or a PerformanceCounter dependency), so
            // it's deliberately left out rather than shipped half-working.
            ["cpu_cores"] = Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture)
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
