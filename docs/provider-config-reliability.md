# AI client config writing: what can go wrong, and what the writer does

`AiProviderConfigService` adds this server's entry to the config of each installed AI client. It runs only on request
(`register_mcp_providers`, `POST /api/register-providers`), never at startup. Auto-writing client configs was removed
earlier as "unreliable" without a recorded reason; this page records the reasons found, how each is handled, and what
is still not solved.

## Failure modes and handling

| Failure mode | Seen where | Handling | `failureReason` |
|---|---|---|---|
| File has comments or trailing commas (JSONC) | VS Code `mcp.json` is JSONC; users annotate it | Refused, file left byte-for-byte alone; the install prompt has the manual snippet | `comments-or-trailing-commas` |
| File is not valid JSON, or the root is not an object, or the root key (`servers` / `mcpServers`) is not an object | Hand-edited or half-written files | Refused, not modified | `invalid-json`, `unexpected-structure` |
| Another process rewrites the file while we edit | `~/.claude.json` (about 190 KB) is rewritten by Claude Code itself, observed changing size between two reads during testing | Optimistic concurrency: re-read just before the swap, redo the edit on the new content if it changed (up to 5 attempts) | `changed-concurrently` after the last attempt |
| File held open without sharing | An editor or antivirus with the file open | Read with `FileShare.ReadWrite`; retry the swap; fail with the OS reason, nothing changed, temp file removed | `locked` |
| The rewrite changes something besides our entry | Number formats (`1.50`, big integers, `1e-7`), unicode and `&<>` escaping, key order, BOM, line endings | Parse with `JsonNode` (numbers keep their text), relaxed escaping, original BOM and newline style kept, then verify in memory and again after the write that the document equals the original apart from the entry; restore the backup if not | `verify-failed` |
| Different shapes per client | `servers`+`type:http` (VS Code), `mcpServers`+`type:http` (Claude Code), no `type` (Cursor), `type:streamableHttp`+`disabled:false` (Cline), `disabled:false` (Sixth) | One entry builder per client | |
| Continue reads many files and its `config.yaml` is YAML | Continue | TokenSaver gets its own `~/.continue/mcpServers/tokensaver.yaml`; the user's config is never touched | |
| Claude Desktop cannot take a remote http entry in its file | Claude Desktop | Reported, not written (add a custom connector) | `unsupported` |
| Client not installed | any | Skipped; no folders or files are created for it. Claude Code's file is never created, only edited | `not-installed` |
| Docs disagreed with each other | `ProviderType` said Cline and Sixth share VS Code's `mcp.json`; `install-clients/sixth-ai.md` says Sixth has its own settings file | Each has its own file, as in the install docs | |
| The entry carries the endpoint token | After endpoint auth (R1) | Token written into `headers.Authorization` when `RequireAuth` is on; the client config files are user-owned | |

Every successful edit of an existing file leaves a timestamped `<file>.bak-<yyyyMMddHHmmssfff>` (latest three kept) and
replaces the file atomically (write beside it, then `File.Replace`).

## Found while debugging on copies of real configs

The writer was run against copies (in a temp profile) of this machine's Claude Code, VS Code, Continue and Claude
Desktop configs; the real files were not touched.

1. **LF files came back as CRLF.** `System.Text.Json` writes the OS newline, which on Windows turned the 5,600-line
   `~/.claude.json` into a file whose every line changed (194,155 to 199,868 bytes for a 300-byte entry). The unit
   tests only covered the CRLF case and did not notice. Fixed: the output is normalised to LF, then to the original
   style (this also fixed a doubled `\r` in the CRLF path). After the fix the same file grows by 107 bytes and 3
   lines. Regression test: `AnLfFile_StaysLf_EvenOnWindows`.
2. **The real `~/.claude.json` changed size between two runs a few minutes apart** (194,155 to 193,951 bytes) with no
   action from us: Claude Code rewrites it continuously. That is the concurrent-writer risk in practice, and why the
   re-read check exists.
3. **PowerShell's `ConvertFrom-Json` rejects `~/.claude.json`** while `System.Text.Json` accepts it (case-different
   duplicate keys, not invalid JSON). Not a problem for the writer, but a shell-based writer would have failed here.

## Not solved

- The window between the final re-read and the swap is small but not zero: if Claude Code writes in it, its change is
  lost (we keep the backup). The CLI route (`claude mcp add`) would avoid this; it is not used.
- Pure formatting changes remain possible: JSON is re-indented with two spaces. Semantics are verified; whitespace is not
  preserved for files that use another indent.
- Not exercised against the real clients: nobody has restarted Claude Code, VS Code, Cursor, Continue, Cline or Sixth
  after a write to confirm each accepts the entry. Header support differs by client version.
- Comments in `mcp.json` block the write; editing JSONC in place would need a text-level editor.
