# TokenSaver.MCP — Health Check Contract

This document is the source of truth for TokenSaver.MCP's `/health` and `/health/ready` HTTP
surface. It exists so `TokenSaver.MCP.Tests` has a fixed spec to assert against, and so anyone
polling these endpoints (the VS Code extension, monitoring, manual curl) knows exactly what to
expect. See the separate rsperry79/SimpleModelServer repo's `docs/router-contract.md` for the equivalent
contract on SimpleModelServer's router — this document mirrors it, adapted to TokenSaver.MCP's
ASP.NET Core `Microsoft.Extensions.Diagnostics.HealthChecks`-based architecture.

## Response shape

Both endpoints use the same JSON writer (`WriteJsonHealthResponse` in `Program.cs`):

```json
{
  "status": "Healthy" | "Degraded" | "Unhealthy",
  "checks": [
    {
      "name": "<check name>",
      "status": "Healthy" | "Degraded" | "Unhealthy",
      "description": "<human-readable summary>",
      "error": null | "<exception message, if the check itself threw>",
      "data": null | { "<arbitrary key>": "<value>" }
    }
  ],
  "totalDuration": <milliseconds>
}
```

**Status codes**: this is standard ASP.NET Core health-check middleware, not custom router logic
like SimpleModelServer's — the default status-code mapping applies: `Healthy` → 200,
**`Degraded` → 200**, `Unhealthy` → 503. A degraded response is still an HTTP success; callers
must inspect the `status` field in the body, not just the status code, to detect degradation.
(This differs from SimpleModelServer's router, which maps degraded to 503 itself.)

## `GET /health`
Runs **every** registered health check (no predicate) — this is the full liveness +
self-diagnostics view. Registered checks today:

- `simpleModelServer` (`SimpleModelServerHealthCheck`) — Healthy/Unhealthy based on SimpleModelServer router
  reachability and whether models are pulled. Whether models are currently loaded in VRAM is
  reported as Healthy either way — via `data["vram_state"]` (`"loaded"` / `"idle"`) — since an idle
  (keep_alive-expired) model is expected steady-state, not a degradation; only the next request
  pays a one-time cold-load delay. Tagged `"ready"`. Also reports host resource stats —
  `data["gpu"]`, `data["vram_used_mb"]`/`data["vram_total_mb"]`/`data["gpu_util_pct"]` (via
  `nvidia-smi`; `"unknown"` if unavailable), `data["ram_used_mb"]`/`data["ram_total_mb"]`
  (system-wide physical memory, not just this process), `data["cpu_cores"]` (logical core count;
  no live CPU utilization % — no simple cross-platform BCL API for that) — informational only,
  never affects the Healthy/Unhealthy classification. `data["models_available"]` mirrors the
  router's `/api/tags`, which is deliberately a single virtual model name, not every concrete
  model pulled — see SimpleModelServer's `docs/router-contract.md`. The real concrete model names only
  ever show up in `data["models_in_vram"]` (proxied verbatim from Ollama's own `/api/ps`).
- `unhandled-fault` (`UnhandledFaultHealthCheck`) — Degraded if an unhandled exception was
  recorded within the last 2 minutes anywhere on this server (see "Fault tracking" below);
  otherwise Healthy. Untagged.
- `self-test` (`SelfTestHealthCheck`) — Degraded if any in-process self-test check fails or
  throws (see "Endpoint self-tests" below); otherwise Healthy. Untagged.

Overall `status` is the worst of all check statuses (standard `HealthCheckService` aggregation
behavior: Unhealthy > Degraded > Healthy).

## `GET /health/ready`
Only runs checks tagged `"ready"` — today, just `simpleModelServer`. This is "is the server usable right
now" (can it reach SimpleModelServer and serve a real request), deliberately **not** affected by a past
unhandled exception or a self-test failure — those are surfaced on `/health` but shouldn't flip
readiness, mirroring SimpleModelServer's `RouterHttpServer` split between `/health` (full
diagnostics) and `/health/ready` (cheap, dependency-only readiness).

## Fault tracking
`FaultTrackingMiddleware` is registered first in the request pipeline (wraps every other
middleware, including `ContextInterceptMiddleware` and the `/mcp` MCP endpoint itself). Any
unhandled exception from any request is recorded into `UnhandledFaultTracker` (message + UTC
timestamp) and then re-thrown unchanged — this middleware only adds observability, it doesn't
change how the exception is otherwise handled (client still sees whatever the framework/hosting
layer normally does with an unhandled exception).

A recorded fault stays "recent" for 2 minutes (`UnhandledFaultTracker.FaultTtl`), then ages out on
its own — there's nothing for `UnhandledFaultHealthCheck` or a periodic loop to "fix"; a stack
trace isn't a dependency outage, so it's a time-boxed signal rather than something recovery logic
acts on. While recent, `unhandled-fault`'s check reports `Degraded` with the exception's message
in `checks[].description` and `checks[].data.lastError`.

## Endpoint self-tests
`SelfTestHealthCheck` exercises a few cheap, in-process, no-network-I/O checks to catch a broken
configuration/wiring bug before a real caller does (this deliberately does not call
SimpleModelServer/Ollama — that's `SimpleModelServerHealthCheck`'s job). Checks currently run:

- `config-bound` — `IOptions<TokenSaverConfig>.Value.OllamaUrl` is non-empty.
- `workspace-root-resolvable` — if `WorkspaceRoot` is configured, the directory actually exists.
- `endpoint-registry-has-endpoint` — `OllamaEndpointRegistry.GetAllEndpoints()` returns at least
  one endpoint (the registry always synthesizes a "default" entry when none are configured, so
  this failing indicates a genuine wiring bug, not just an empty config).

Each check is independent — one throwing doesn't stop the rest from running — and a failing or
throwing check is caught and reported as part of the aggregated `self-test` health check result
rather than surfacing as a 500 from `/health` itself.

## Not covered by this contract
- The exact wording of `checks[].description` for `simpleModelServer` (see `SimpleModelServerHealthCheck` for
  current text) — only its Healthy/Degraded/Unhealthy classification and the presence of
  `data.models_available`/`data.models_in_vram` when relevant.
- `totalDuration`'s precision or units beyond "milliseconds, as a floating-point number".
