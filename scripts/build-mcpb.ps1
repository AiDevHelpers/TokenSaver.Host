#Requires -Version 7
<#
.SYNOPSIS
    Builds and packages TokenSaver.MCP as a Claude Desktop Extension (.mcpb).

.DESCRIPTION
    1. Publishes TokenSaver.MCP as a self-contained win-x64 single-file executable into a
       staging directory.
    2. Writes manifest.json describing the extension to Claude Desktop.
    3. Zips everything into build/dist/tokensaver.mcpb.

    The resulting .mcpb can be double-clicked in Claude Desktop (or Cowork) for
    one-click installation — no terminal, no JSON editing.

    This package does NOT bundle SimpleModelServer (that lives in the separate
    rsperry79/SimpleModelServer repo). TokenSaver.MCP talks to SimpleModelServer's router over HTTP
    (default http://localhost:11435, see TokenSaverConfig.OllamaUrl) — SimpleModelServer must
    be installed/running separately. If a combined .mcpb bundling both is needed, that's a
    separate cross-repo composition step (see F:\mcp's own build-mcpb.ps1).

.PARAMETER Version
    Semantic version written into the manifest. Defaults to "1.0.0".

.PARAMETER Configuration
    dotnet build configuration. Defaults to "Release".

.PARAMETER Runtime
    .NET runtime identifier. Defaults to "win-x64".

.EXAMPLE
    .\build-mcpb.ps1
    .\build-mcpb.ps1 -Version 1.2.0
#>
[CmdletBinding()]
param(
    [string] $Version       = "1.0.0",
    [string] $Configuration = "Release",
    [string] $Runtime       = "win-x64"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot = $PSScriptRoot | Split-Path -Parent
$DistDir  = Join-Path $RepoRoot "build\dist"
$StageDir = Join-Path $RepoRoot "build\staging\mcpb"

function Write-Step([string]$msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)   { Write-Host "    OK  $msg" -ForegroundColor Green }

$CsProj = Join-Path $RepoRoot "src\Host\TokenSaver.MCP\TokenSaver.MCP.csproj"
$OutDir = Join-Path $StageDir "TokenSaver.MCP"
$Exe    = Join-Path $StageDir "TokenSaver.MCP\TokenSaver.MCP.exe"

# ── 1. Publish binary ──────────────────────────────────────────────────────────

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET SDK not found. Install it from https://dot.net and re-run."
}
Write-Ok ".NET SDK $(dotnet --version)"

# Clear stale staging content so we don't zip leftovers from a prior run
if (Test-Path $StageDir) {
    Write-Step "Clearing staging dir"
    Remove-Item $StageDir -Recurse -Force
}

Write-Step "Restoring TokenSaver.MCP"
dotnet restore $CsProj --runtime $Runtime
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed for TokenSaver.MCP (exit $LASTEXITCODE)" }

Write-Step "Publishing TokenSaver.MCP"
$null = New-Item -ItemType Directory -Force -Path $OutDir

dotnet publish $CsProj `
    --configuration $Configuration `
    --runtime       $Runtime `
    --self-contained true `
    --no-restore `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    --output        $OutDir

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for TokenSaver.MCP (exit $LASTEXITCODE)" }

if (-not (Test-Path $Exe)) {
    throw "Publish succeeded but exe not found at: $Exe"
}

$size = (Get-Item $Exe).Length / 1MB
Write-Ok "TokenSaver.MCP.exe  ($([math]::Round($size, 1)) MB)"

# ── 2. Write manifest.json ─────────────────────────────────────────────────────

Write-Step "Writing manifest.json"

$manifest = [ordered]@{
    manifest_version = "0.1"
    name             = "token-saver"
    display_name     = "TokenSaver MCP"
    version          = $Version
    description      = "Local Ollama-backed MCP server with context hygiene, draft-pair generation, and agent routing."
    long_description = @"
TokenSaver gives Claude a private, local AI backend powered by Ollama.

Features:
- CleanAndChat: context-cleans prompts and sends them to a local model
- BuildConciseContext: builds a token-efficient workspace summary
- ContextHygiene: inspects and reports on the local context cache

Requires a running SimpleModelServer router (installed separately from
rsperry79/SimpleModelServer) — it manages the Ollama process lifecycle, pulls required
models, and runs a router that fans prompts to both a fast draft model and a
full-size main model in parallel.

All inference runs entirely on your machine — no data leaves your device.
"@
    author           = [ordered]@{ name = "Richard Sperry" }
    repository       = [ordered]@{ type = "git"; url = "https://github.com/rsperry79/TokenSaver" }
    server           = [ordered]@{
        type        = "binary"
        entry_point = "TokenSaver.MCP/TokenSaver.MCP.exe"
        mcp_config  = [ordered]@{
            command = "`${__dirname}/TokenSaver.MCP/TokenSaver.MCP.exe"
            args    = @()
            env     = [ordered]@{
                FASTMCP_OllamaUrl = "http://localhost:11435"
            }
        }
    }
    tools            = @(
        [ordered]@{ name = "CleanAndChat";       description = "Context-cleans a prompt then calls the local Ollama model" }
        [ordered]@{ name = "BuildConciseContext"; description = "Builds a token-efficient workspace summary into .local-mcp/" }
        [ordered]@{ name = "ContextHygiene";      description = "Inspects and reports on the .local-mcp/ context cache" }
    )
    tools_generated  = $true
    keywords         = @("ollama", "local", "mcp", "context", "ai", "offline")
    license          = "MIT"
    compatibility    = [ordered]@{ platforms = @("win32") }
}

$manifestPath = Join-Path $StageDir "manifest.json"
$manifest | ConvertTo-Json -Depth 10 | Set-Content -Path $manifestPath -Encoding UTF8
Write-Ok $manifestPath

# ── 3. Package as .mcpb (zip) ─────────────────────────────────────────────────

Write-Step "Packaging -> build/dist/tokensaver.mcpb"

$null = New-Item -ItemType Directory -Force -Path $DistDir
$OutFile = Join-Path $DistDir "tokensaver.mcpb"

if (Test-Path $OutFile) { Remove-Item $OutFile -Force }

Compress-Archive -Path "$StageDir\*" -DestinationPath $OutFile -CompressionLevel Optimal

$outSize = (Get-Item $OutFile).Length / 1MB
if ($outSize -lt 1) {
    throw "Output is suspiciously small ($([math]::Round($outSize * 1024)) KB) — something went wrong."
}

Write-Ok "$OutFile  ($([math]::Round($outSize, 1)) MB)"

# ── Done ──────────────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  Package : $OutFile  ($([math]::Round($outSize, 1)) MB)"
Write-Host ""
Write-Host "Note: this package bundles only TokenSaver.MCP. Install SimpleModelServer separately" -ForegroundColor Yellow
Write-Host "(see rsperry79/SimpleModelServer) for TokenSaver to have a router to talk to." -ForegroundColor Yellow
Write-Host ""
Write-Host "To install: double-click tokensaver.mcpb in Claude Desktop / Cowork." -ForegroundColor Yellow
