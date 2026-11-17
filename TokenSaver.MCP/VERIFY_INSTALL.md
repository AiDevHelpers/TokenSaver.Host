# Verifying TokenSaver.MCP is fully working

Use this after TokenSaver.MCP is installed, started, and wired up to a client (see
`TokenSaver/TokenSaver.VsCodeExt/INSTALL_PROMPT.md`), and only **after**
`SimpleModelServer/SimpleModelServer/VERIFY_INSTALL.md` has already passed — most TokenSaver.MCP tools
call through to SimpleModelServer, so verifying TokenSaver first would misattribute a SimpleModelServer
failure as a TokenSaver bug.

Default port: `11436`. Most of this server's surface is MCP tools (invoked through whichever
client you configured — Claude Code, Copilot Chat, Cursor, etc. — over the `/mcp` endpoint), not
plain HTTP, so most tests here are "ask the connected AI client to call tool X and check the
result," not curl commands. Run these in order; stop and report at the first failure.

## 1. Health

```bash
curl -s http://localhost:11436/health
curl -s http://localhost:11436/health/ready
```

`/health/ready` additionally checks SimpleModelServer reachability — if `/health` passes but
`/health/ready` doesn't, the issue is SimpleModelServer connectivity from TokenSaver's side, not
TokenSaver itself. Re-check `SimpleModelServer/SimpleModelServer/VERIFY_INSTALL.md` first.

## 2. Confirm the client sees the server and its tools

In whichever client you configured, confirm `tokensaver` (or the connector's configured name)
shows as connected, and that a tool listing shows all of the tools below. If tools are missing
from the list, the MCP connection itself is broken — re-check the client config file, not the
tool implementations.

## 3. Tool: `Help`

Ask the client: *"Call the Help tool with no arguments."*
Expect a list of available TokenSaver.MCP tools grouped by category, not an error. This is the
lowest-risk tool to start with — it doesn't touch SimpleModelServer or the filesystem.

## 4. Tool: `CleanAndChat` (exercises the SimpleModelServer link end-to-end through MCP)

Ask the client: *"Call CleanAndChat with prompt: 'Reply with exactly the word: pong'."*
Expect a real model response (not an error, not an empty string). This is the single most
important test in this file — it proves the full chain (client → MCP → SimpleModelServer router →
Ollama → model → back) works, not just that each piece is independently alive.

## 5. Tool: `ListOllamaEndpoints`

Ask the client: *"Call ListOllamaEndpoints."*
Expect a list of configured Ollama endpoints with health/stats, including the one SimpleModelServer is
using. An empty or error result here means TokenSaver can't see SimpleModelServer's endpoint
configuration even if `CleanAndChat` worked — worth flagging as a config-visibility gap.

## 6. Tool: `CodeIndex` (workspace awareness)

Ask the client: *"Call CodeIndex with action 'scan' against the current workspace root."*
Expect a scan result (even if the workspace is small/empty, expect a valid empty-ish report, not
an error). Confirms the tool can actually read the filesystem it's pointed at.

## 7. Tool: `ContextDiag`

Ask the client: *"Call ContextDiag with action 'describe'."*
Expect a description of the context pipeline's current state. A crash here often means a
workspace-root resolution problem — check whether `root`/`workspaceRoot` needs to be passed
explicitly for this client/session.

## 8. Tool: `BuildConciseContext`

Ask the client: *"Call BuildConciseContext with prompt: 'summarize this workspace'."*
Expect a workspace context summary (file list/summary text), not an error. Exercises the
context-building path independently of chat.

## 9. Tool: `EditFile` (dry run only — do not apply a real edit during verification)

Ask the client: *"Call EditFile with action 'apply', a harmless oldText/newText pair on a
throwaway or test file, and dryRun: true."*
Expect a preview/diff-style result with no actual file changed. Confirms the file-edit tool
loads and validates without needing to risk a real mutation as part of verification.

## 10. Raw HTTP endpoint: `/api/register-providers`

Needs the endpoint token (`Authorization: Bearer <token>`, see `INSTALL_PROMPT.md`). It writes, so it is a POST:

```bash
curl -s -X POST "http://localhost:11436/api/register-providers" -H "Authorization: Bearer $TOKEN"
```

It adds the entry to every installed AI client's config and returns one result per client (`success`,
`wasAlreadyConfigured`, `configPath`, `message`, `failureReason`). Run it on a throwaway profile or expect
your real client configs to change (a timestamped `.bak-*` copy is kept). A client whose file cannot be rewritten
faithfully is reported with a `failureReason` instead of being modified; see
`docs/Host-MCP/provider-config-reliability.md`.

## What "fully working" means

Steps 1–2 confirm the connection; steps 3–9 confirm each major tool category (help, chat-through-
SimpleModelServer, endpoint visibility, filesystem/code awareness, context building, file editing) works
without error; step 10 confirms the deprecated endpoint fails safe rather than erroring. If step 4
(`CleanAndChat`) fails but SimpleModelServer's own `VERIFY_INSTALL.md` passed standalone, the problem is
specifically in TokenSaver's routing/config pointing at SimpleModelServer — check its config for the
correct SimpleModelServer router port/host before assuming a code bug.
