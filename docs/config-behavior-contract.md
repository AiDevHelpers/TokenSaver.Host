# Behavior Config & Ignore Overrides — Contract

Covers `TokenSaver.Config` (the `BehaviorConfigService`, `WorkspaceIgnoreFilter`,
`GitignorePrompt`) and the `ManageConfig` MCP tool. This is a public surface (config file formats
consumed by users + an MCP tool exposed to AI clients), so this doc is the guaranteed contract.

## Config files

Two files, identical JSON shape. **Local always wins over global, per-property.**

| Scope  | Path                                        |
| ------ | ------------------------------------------- |
| Local  | `{workspaceRoot}/.tokensaver/config.json`   |
| Global | `%ProgramData%\TokenSaver\config.json`      |

The local path can be overridden outright (bypassing `{workspaceRoot}`-relative resolution) via the
`TOKENSAVER_LOCAL_CONFIG_PATH` env var, or by setting `TokenSaver:BehaviorConfigPath` in the server
config (exported to that env var at startup — see `Program.cs`). `scripts/run-dev-server.ps1` uses
this to point the dev loop at `.tokensaver/dev-config.json` instead of the real
`.tokensaver/config.json`. The global path has an equivalent override,
`TOKENSAVER_GLOBAL_CONFIG_DIR` (a directory, not a file path — tests use this to avoid touching the
real machine-wide file).

Shape (all keys optional; a missing key means "unset"):

```json
{
  "addTokenSaverToGitignore": true,
  "autoCodeHelperEnabled": false,
  "requireAnchorMatch": true,
  "canDoThreshold": 0.70,
  "deferThreshold": 0.40,
  "confidenceThreshold": 0.65,
  "modelCallThreshold": 0.55,
  "wasteThreshold": 0.40,
  "learningRate": 0.10,
  "enableDistill": true,
  "enableTerse": true,
  "enableRawRetention": true,
  "enableSavingsTracking": true,
  "enableSummaryEmbedding": true,
  "terse": false,
  "context": {
    "debug": false
  },
  "agentConfig": {
    "enablePromptCaching": true,
    "cacheTtlSeconds": 300,
    "minCacheableTokens": 1024,
    "anthropic": {
      "enableContextEditing": false,
      "contextEditingTriggerTokens": 100000,
      "contextEditingKeepToolUses": 3
    },
    "deepSeek": {},
    "qwen": {},
    "openSource": {
      "useFlashAttention": true,
      "kvCacheQuantization": "q8_0",
      "numCtx": 8192
    }
  }
}
```

- `addTokenSaverToGitignore` (`bool?`) — whether `.tokensaver/` should be in `.gitignore`.
  `null`/absent = never answered (drives the pending prompt below); `true`/`false` = the recorded
  answer.
- `autoCodeHelperEnabled`, `requireAnchorMatch`, `canDoThreshold`, `deferThreshold`,
  `confidenceThreshold`, `modelCallThreshold`, `wasteThreshold`, `learningRate` — override the
  matching `CodeHelperConfig` appsettings default. `null`/absent = keep the appsettings value.
  Note: `autoCodeHelperEnabled` sits *below* `CalibrationService`'s own persisted
  workspace/user override in the resolution order — a calibration-learned override still wins
  over this default.
- `enableDistill`, `enableTerse`, `enableRawRetention`, `enableSavingsTracking`,
  `enableSummaryEmbedding`, `terse` — override the matching `TokenSaverConfig.TokenEconomy`
  appsettings default. `null`/absent = keep the appsettings value.
- `agentConfig` — provider-native context-reduction settings for the four
  `TokenSaver.AgentHelpers.*` families (Anthropic, DeepSeek, Qwen, OpenSource/Ollama). See
  **Agent config (provider context-reduction settings)** below.
- `context.debug` (`bool?`) — off by default. When `true`, `ContextCleanerService` writes the raw
  (pre-pipeline) and cleaned (post-pipeline) context text for every `CleanContextAsync` run to
  `{cwd}/.tokensaver/debug/{timestamp}_{sessionId}.{raw,clean}.txt`, and logs a warning if the
  cleaned text did not shrink relative to the raw input (`finalTokens >= rawTokens`). A diagnostic
  aid for verifying the cleaning pipeline is actually reducing token count — not needed in normal
  operation. `null`/absent = keep the appsettings value (`ContextHelperConfig.Debug`, `false`).

### Context pipeline self-test

`ContextCleanerService.RunSelfTestAsync` — exposed as `ContextDiag(action: "self-test")` — is an
on-demand functional check: it runs a synthetic, deliberately redundant payload (30+ duplicated
`[INFO]`/`[ERROR]` log-style lines) through the real `CleanContextAsync` pipeline and reports
whether the output actually shrank (`ContextSelfTestResult.Passed` = `CleanedTokens < RawTokens`).
The payload is built so `LogStrippingStage` (Stage 2) alone guarantees compression, independent
of whether the optional embedding/summarization stages' network calls to Ollama succeed — those
stages degrade gracefully on failure (drop nothing / skip), so a failing self-test reliably means
a pipeline stage regressed to a no-op or is inflating content, not that Ollama was unreachable.
Returns the raw and cleaned token counts, savings percent, and the per-stage narrative
(`PipelineSummary.NarrativeSummary`) for diagnosing which stage failed.

**Gated on `context.debug`** (see above), and deliberately hidden rather than just rejected: the
`action` parameter's `[Description]` never lists `'self-test'`, and calling
`ContextDiag(action: "self-test")` while `context.debug` is `false` (the default) returns the
exact same "Unknown action..." text an unrecognized action string would — there is no
distinguishable "disabled" response that would tip off a calling agent that the action exists.
This keeps the diagnostic out of the normal tool surface entirely; a user/operator who wants it
must already know to set `context.debug=true` first (e.g. via `ManageConfig` or a hand-edited
`.tokensaver/config.json`). Verified by `ContextDiagnosticsToolsTests`
(`SelfTest_DebugDisabled_IsHiddenBehindUnknownActionMessage`,
`SelfTest_DebugEnabled_RunsRealPipeline_ReportsPass`, `SelfTest_SyntheticPayload_ActuallyShrinks`).

These overrides are applied in place onto the running `CodeHelperConfig`/`TokenEconomyConfig`
singletons by `TokenSaver.CodeHelper.Configuration.BehaviorConfigOverlay` — once at startup and
again every time `BehaviorConfigService.ConfigChanged` fires (i.e. on every local/global
config-file edit), so a `set` via `ManageConfig` or a hand-edited config.json takes effect without
a server restart. Every consumer of these two config classes holds a direct reference to the same
`IOptions<T>.Value` instance, so the in-place mutation is visible everywhere immediately.

**Merge rule** (`TokenSaverBehaviorConfig.Merge`): for each property, take the local value if it is
non-null, else the global value, else null. Verified by `TokenSaverBehaviorConfigTests`.

**Reload**: `BehaviorConfigService` reads both files at first access for a workspace and re-reads
them whenever either changes on disk (`FileSystemWatcher` on both folders). A malformed or
unreadable file is logged and treated as absent — it never throws to a caller. Verified by
`BehaviorConfigServiceTests`.

## Agent config (provider context-reduction settings)

`agentConfig` (`AgentBehaviorConfig`) captures each AI provider's own built-in mechanism for
shrinking the tokens it actually bills/processes as context grows — distinct from the
`ContextBehaviorConfig`/`TokenEconomyBehaviorConfig` trimming TokenSaver itself performs before
handing context off. One section per `TokenSaver.AgentHelpers.*` family:

| Family                        | Native mechanism                                                                 |
| ------------------------------ | --------------------------------------------------------------------------------- |
| Anthropic (`TokenSaver.AgentHelpers.Anthropic`) | Explicit `cache_control` prompt-cache breakpoints (5m/1h TTL) + server-side context editing (`clear_tool_uses_20250919`) that auto-clears old tool results past a token trigger. |
| DeepSeek (`TokenSaver.AgentHelpers.DeepSeek`)   | Always-on disk-based context caching — automatic, not configurable per request.   |
| Qwen (`TokenSaver.AgentHelpers.Qwen`)           | Explicit `cache_control` markers (same shape as Anthropic's, ~5 min fixed TTL) plus an always-on implicit cache. |
| OpenSource (`TokenSaver.AgentHelpers.OpenSource`, Ollama-hosted Llama/Mistral/Gemma) | No server-side cache; closest analogs are Flash Attention + KV-cache quantization (`OLLAMA_KV_CACHE_TYPE`) and `num_ctx`. |

**Global defaults, per-family overrides**: `enablePromptCaching`, `cacheTtlSeconds`, and
`minCacheableTokens` are the three settings common to every family that supports caching at all
(Anthropic and Qwen use them directly; DeepSeek ignores them since its caching isn't
configurable but keeps the field for uniform handling; OpenSource repurposes
`enablePromptCaching` to gate whether Flash Attention/KV quantization apply, since there's no
real cache to switch). Each nested section (`anthropic`, `deepSeek`, `qwen`, `openSource`)
declares the *same-named* property to override just that family's value — same merge rule as
everywhere else: a set per-family value wins over the top-level `agentConfig` value, which wins
over "unset" (provider default). Families also carry fields with no cross-provider equivalent:

- `anthropic.enableContextEditing` / `contextEditingTriggerTokens` / `contextEditingKeepToolUses`
  — server-side context editing has no analog in the other three families.
- `openSource.useFlashAttention` / `kvCacheQuantization` / `numCtx` — Ollama-specific memory/
  context-window tuning; `cacheTtlSeconds`/`minCacheableTokens` are meaningless for it and
  intentionally not read from that section.

Overlaid the same way as the other sections — once at startup and on every `ConfigChanged` —
by the relevant `TokenSaver.AgentHelpers.*` adapter before it builds a provider request.

### Automatic detection at session start

Unlike `CodeHelper`/`TokenEconomy`/`Context`, `agentConfig`'s per-family section is not something
a user has to pick manually — `AgentConfigResolver` (`TokenSaver.MCP.Services`, Host-level: it
bridges `TokenSaver.AgentHelpers.Core`'s caller detection to `TokenSaver.Config`'s behavior config,
two projects that don't reference each other) detects which family is calling and resolves that
family's section automatically:

1. `ICallerDetectionService.Detect` runs the same waterfall the context-formatting adapters use
   (MCP `ClientInfo` → `X-Model-Hint`/`X-LLM-Model` header → `User-Agent` → configured Ollama model
   → default) to produce a `CallerInfo`.
2. `AgentAdapterRegistry.Resolve` picks the matching `IAgentAdapter` (`AnthropicAgentAdapter`,
   `DeepSeekAgentAdapter`, `QwenAgentAdapter`, `OpenSourceAgentAdapter`, or the no-op default) —
   its `Family` name (`"Anthropic"`, `"DeepSeek"`, `"Qwen"`, `"OpenSource"`) selects which
   `agentConfig` subsection to read.
3. `AgentConfigResolver.Resolve` merges that subsection over the top-level `agentConfig` defaults
   (same null-means-fall-through rule as `Merge` everywhere else) and returns an
   `EffectiveAgentSettings` — only the fields relevant to the detected family are populated.

The HTTP transport is stateful (each client gets an `Mcp-Session-Id`; see "Workspace root per session" below), but agent detection stays per request: there is no cached per-session agent, so
`AgentDetectionMiddleware`
(runs before `ContextInterceptMiddleware`) instead peeks the `initialize` handshake on every
connection and logs what was detected (`family`, `client`, `model`, `promptCaching`,
`cacheTtl`) at Information level; `AutoWorkflowTools`/`CodeHelperTools`/`LocalModelHelperTools`
independently resolve the caller per tool-call the same way they already resolve `ContextFormat`.
`ManageConfig(action: "get")` also surfaces the current detection under `detectedAgent` (family,
client name, model hint, detection source, and the effective settings) so a user can see exactly
what was auto-applied without reading logs.

An explicit override written via `ManageConfig(action: "set-agent", family: "anthropic", ...)`
always wins over auto-detection — detection only decides *which* family's section to read; the
section's own local-over-global merge behaves like every other config value. `family` may be
omitted/`"global"` to set the shared default, or one of `anthropic`/`deepSeek`/`qwen`/`openSource`
to set that family's override. Note `deepSeek.cacheTtlSeconds`/`minCacheableTokens` are accepted by
the schema for symmetry but never read — DeepSeek's context caching isn't configurable.

## Ignore-override files (gitignore syntax)

Both live under `.tokensaver/` and use `.gitignore` glob/anchor/negation syntax.

| File                | Effect                                                                     |
| ------------------- | -------------------------------------------------------------------------- |
| `.ignore-additional` | Extra paths to **exclude** from indexing/context that are *not* in `.gitignore`. |
| `.ignore-bypass`     | Paths that *are* gitignored/excluded but should still be **indexed/included**.   |

`WorkspaceIgnoreFilter` composes three gitignore-syntax sources — `.gitignore`,
`.ignore-additional`, and `.ignore-bypass`:

```
ignored = (gitignore ∨ additional) ∧ ¬bypass
```

Bypass wins over everything. Used by the three indexing walkers (`WorkspaceIndexService`,
`FolderTreeManager`, `GitStalenessService`). Verified by `WorkspaceIgnoreFilterTests`.

`ContextDiag(action: "context-exclude")` writes to `.ignore-additional` (as directory patterns,
e.g. `some/dir/`) rather than a separate JSON file — there used to be a second,
JSON-formatted `.tokensaver/context-exclusions.json` with the same "hide from context, keep in
git" purpose, but it only supported exact/prefix directory matching, a strict subset of what
`.ignore-additional`'s gitignore glob syntax already covers, so it was retired in favor of the
one gitignore-style mechanism. A leftover `context-exclusions.json` from an older workspace is
no longer read by anything; move any entries into `.ignore-additional` by hand
(`ManageConfig(action: "ignore-additional", paths: [...])` or `ContextDiag(action:
"context-exclude", ...)` both append to it).

## `ManageConfig` MCP tool

| action                | inputs                        | behavior |
| --------------------- | ----------------------------- | -------- |
| `get`                 | —                             | Returns merged config, both config paths, and `gitignorePromptPending`. |
| `set-gitignore-pref`  | `add` (bool), `scope` (`local`/`global`) | Records the answer to that scope. When `add=true` and `.tokensaver` isn't already covered, appends `.tokensaver/` to `.gitignore`. |
| `ignore-additional`   | `paths[]`                     | Appends de-duped patterns to `.ignore-additional`. |
| `ignore-bypass`       | `paths[]`                     | Appends de-duped patterns to `.ignore-bypass`. |
| `set`                 | `field`, `value`, `scope` (`local`/`global`) | Sets one behavior-config field (see the field lists above). `value` is parsed per field type; pass `value` omitted/`"null"` to clear the override. Applied live via `BehaviorConfigOverlay` — no restart needed. |

`root` optionally overrides the workspace root on every action.

## Pending gitignore prompt

`GitignorePrompt.ShouldAsk(config, root)` is true when `addTokenSaverToGitignore` is unset **and**
`.tokensaver` is not already covered by `.gitignore`. When true, `BuildConciseContext` appends an
`<!-- ℹ gitignore: … -->` note to its result asking the AI to relay the question (add? global or
local?) and then call `ManageConfig(action: "set-gitignore-pref", …)`. The note stops appearing
once the answer is recorded.

## Request interception (`contextHelper.autoIntercept`, `codeHelper.autoEnabled`)

`ContextInterceptMiddleware` only touches `tools/call` requests for an **allowlist** of tools whose
`prompt` argument is natural language (today: `build_adaptive_context`). Every other tool, and any
malformed body, is forwarded unchanged. It never answers a request and never writes a file.

- `contextHelper.autoIntercept` (default `true`): clean the prompt through the context pipeline.
- `codeHelper.autoEnabled` (default **`false`**, opt-in; `Calibration(action: 'toggle')` overrides it
  per workspace): classify code-build prompts with the draft model. Architecture or uncertain requests
  get guidance prefixed to the prompt. Concrete code tasks pass through.

Writing code with the draft model is the explicit `code_work` tool (`start` / `continue`); it is
refused with `AUTO_DISABLED` when `autoEnabled` resolves to false. To add a tool to the allowlist,
change `ContextInterceptMiddleware._interceptTools` and the tests in the same change.

## Workspace root per session

The HTTP transport is stateful. `WorkspaceScopeMiddleware` opens a `WorkspaceScope` for every request,
seeded with the root that request's `Mcp-Session-Id` resolved earlier (`WorkspaceSessionRoots`).
`TokenSaverConfig.WorkspaceRoot` answers for the request being served: the session's root, else the
configured default (`TokenSaver:WorkspaceRoot` / `TOKENSAVER_WorkspaceRoot`) — never another client's
root. The root comes from the client's MCP roots (`WorkspaceRootSessionResolver`, 5 s timeout) or an
explicit `root` argument. Outside a request (background watchers, health checks) it answers the most
recently resolved root. A `DELETE /mcp` ends the session and drops its root.

Known limits: `BehaviorConfigOverlay` still applies one workspace's `.tokensaver/config.json` onto the
shared options (last resolved wins), and `SIMPLEMODELSERVER_WORKSPACE` is a process environment variable.

## Path and command confinement

Tools that read, write or run something at a client-supplied path are confined to the session's workspace
root (`WorkspacePathGuard`): the path is made absolute, links and junctions are followed, and the real
location must be inside the root. `..`, absolute paths elsewhere and UNC paths are refused with
`outside the workspace root`; an unknown root is refused with `No workspace root`. Covered: `edit_file` (and
rollback, whose backup source must be a backup under the configured `BackupPath`), `session_snapshot` /
`session_restore` (including the `full_blob_path` stored in a snapshot), `build_watch`, `diff_summarize`,
`run_tests` / `lint_fix` (working directory), `check_code` / `fix_code`.

`run_tests` and `lint_fix` also launch only an executable from `TokenSaver:AllowedCommands` (comma
separated; empty = `dotnet, npm, npx, pnpm, yarn, pytest, python, python3, go, cargo, mvn, gradle`). The
executable must be a bare name; shells (`cmd`, `powershell`, `sh`) and paths are refused. The rest of the
line is passed as arguments, never to a shell. An allowed tool such as `npm` or `dotnet` can still run
project scripts, so this narrows the surface rather than sandboxing it. `container_exec` and the WSL
tools are not covered.

## Endpoint authentication

`/mcp` and `/api/*` are protected by `EndpointAuthMiddleware` (first in the pipeline). A request is refused when:

1. its `Host` is not loopback (`localhost`, `127.0.0.1`, `[::1]`, `*.localhost`): **403**;
2. it carries any `Origin` header (a browser page; MCP clients and both extensions send none): **403**;
3. `TokenSaver:RequireAuth` (default `true`) is on and it lacks `Authorization: Bearer <token>`: **401** with
   `WWW-Authenticate: Bearer`.

`/health` and `/health/ready` need no token.

The token is 256 random bits, created on first start at `TokenSaver:AuthTokenPath` (default
`%ProgramData%\TokenSaver\auth-token`; `%LOCALAPPDATA%\TokenSaver\auth-token` when ProgramData is not writable, as in
a dev run). Its ACL grants SYSTEM, Administrators and the creating account full control, and the local group
`TokenSaver:AuthGroupName` (default **TokenSaver Users**) read. The installer creates the group and adds the installing
user. Members are managed with `GET`/`POST`/`DELETE /api/auth/users` (list + candidate local accounts, add `{ "account" }`,
remove `?account=`), used by "TokenSaver: Manage Users" in VS Code and the Users card in the Visual Studio Settings tab.
Account names are validated (`name` or `DOMAIN\name`, no path or shell characters).

Clients: VS Code reads the token file itself (`tokenSaver.auth.tokenPath`); AI clients put it in their MCP config's
`headers` (see `install-clients/*.md`). A 401 from a health probe means "server up, token not accepted", not "restart it".
Limits: any member of the group (or administrator) can read the token, so this separates local users, browsers and
remote hosts from the service, not processes of the same user; the service still runs as LocalSystem.
