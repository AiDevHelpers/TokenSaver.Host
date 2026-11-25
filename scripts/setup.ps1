#Requires -Version 7
<#
.SYNOPSIS
    Builds TokenSaver.MCP and registers it with Claude Desktop.

.DESCRIPTION
    1. Publishes TokenSaver.MCP as a self-contained win-x64 executable into
       <repo root>\build\publish\.
    2. Writes appsettings.json in the TokenSaver.MCP publish output with ClaudeAutoStartProjectPath
       and WorkspaceRoot set to the correct paths.
    3. Patches claude_desktop_config.json so Claude Desktop auto-starts TokenSaver via stdio.

    This script does NOT build or install SimpleModelServer — that lives in a separate repo
    (rsperry79/SimpleModelServer) with its own setup.ps1. TokenSaver.MCP talks to SimpleModelServer's
    router over HTTP (see TokenSaverConfig.OllamaUrl, default http://localhost:11435) once both
    are running; there's no build-time dependency between the two.

.PARAMETER PublishRoot
    Output directory for published binaries. Defaults to build\publish\ at the repo root,
    alongside build\staging\ and build\dist\ (this script lives in scripts\, not the
    repo root itself — see $RepoRoot below).

.PARAMETER Runtime
    .NET runtime identifier. Defaults to win-x64.

.PARAMETER SkipBuild
    Skip dotnet publish and go straight to config patching. Useful when re-running after a build.

.EXAMPLE
    .\scripts\setup.ps1
    .\scripts\setup.ps1 -SkipBuild
    .\scripts\setup.ps1 -PublishRoot C:\Tools\TokenSaver
#>
[CmdletBinding()]
param(
    [string] $PublishRoot = "",
    [string] $Runtime     = "win-x64",
    [switch] $SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# This script lives in scripts\, not the repo root — resolve up one level, same
# pattern as publish-tokensaver-for-wix.ps1 in this same directory.
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

if ([string]::IsNullOrWhiteSpace($PublishRoot)) {
    $PublishRoot = Join-Path $RepoRoot "build\publish"
}

# ── Helpers ────────────────────────────────────────────────────────────────────

function Write-Step([string]$msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)   { Write-Host "    OK  $msg" -ForegroundColor Green }
function Write-Warn([string]$msg) { Write-Host "    WARN $msg" -ForegroundColor Yellow }

function Get-ClaudeConfigPath {
    # MSIX (Store) install redirects writes to a VFS location — prefer it when present.
    $msixPath = Join-Path $env:LOCALAPPDATA `
        "Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Claude\claude_desktop_config.json"
    $stdPath  = Join-Path $env:APPDATA "Claude\claude_desktop_config.json"

    $msixDir = Split-Path $msixPath
    if (Test-Path $msixDir) { return $msixPath }
    return $stdPath
}

function Read-JsoncFile([string]$path) {
    # Returns a hashtable; tolerates JSONC (// comments, trailing commas).
    if (-not (Test-Path $path)) { return @{} }

    $text = Get-Content $path -Raw -Encoding UTF8

    # Strip // line comments
    $text = $text -replace '(?m)//[^\r\n]*', ''
    # Strip /* block comments */
    $text = $text -replace '(?s)/\*.*?\*/', ''
    # Remove trailing commas before } or ]
    $text = $text -replace ',(\s*[}\]])', '$1'

    try {
        return $text | ConvertFrom-Json -AsHashtable
    }
    catch {
        Write-Warn "Could not parse existing config at $path — starting fresh."
        return @{}
    }
}

function Write-JsonFile([string]$path, [hashtable]$data) {
    $dir = Split-Path $path
    if ($dir -and -not (Test-Path $dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }
    $data | ConvertTo-Json -Depth 10 | Set-Content -Path $path -Encoding UTF8
}

# ── 1. Prerequisites ───────────────────────────────────────────────────────────

Write-Step "Checking prerequisites"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET SDK not found. Install it from https://dot.net and re-run."
}
Write-Ok ".NET SDK $(dotnet --version)"

# The wix .NET global tool is required by scripts\build-vsix.ps1 (packages the VSIX's
# bundled TokenSaver.Setup MSI) and by a plain `dotnet build`/Visual Studio "Build Solution" of
# TokenSaver.slnx (TokenSaver.Setup .wixproj auto-publish + WiX-build targets). Pinned to 6.0.0
# to match the WixToolset.Sdk/6.0.0 the .wixproj files reference — a mismatched CLI/SDK version
# (e.g. a newer `wix` tool against an older SDK package reference) is a plausible source of build
# breakage even though the two are usually close-to-compatible across the v6/v7 line. If Visual
# Studio reports "wix CLI not found on PATH" even after installing it here, fully exit and
# restart Visual Studio — it caches its process environment at launch and won't pick up a PATH
# change made afterward.
$wixCmd = Get-Command wix -ErrorAction SilentlyContinue
if (-not $wixCmd) {
    Write-Warn "wix CLI not found on PATH. Installing wix 6.0.0 (dotnet tool install -g wix --version 6.0.0)..."
    dotnet tool install -g wix --version 6.0.0
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet tool install -g wix --version 6.0.0 failed (exit $LASTEXITCODE). Install manually and re-run."
    }
    Write-Ok "wix 6.0.0 installed. Restart your terminal (and Visual Studio, if open) to pick up the updated PATH."
} else {
    $wixVersionOutput = (wix --version) 2>&1 | Select-Object -First 1
    if ($wixVersionOutput -notmatch '^6\.') {
        Write-Warn "wix CLI found but is version '$wixVersionOutput', not 6.x (expected to match WixToolset.Sdk/6.0.0 in the .wixproj files). To switch: dotnet tool uninstall -g wix; dotnet tool install -g wix --version 6.0.0"
    } else {
        Write-Ok "wix CLI $wixVersionOutput"
    }
}

# ── 2. Publish TokenSaver.MCP ──────────────────────────────────────────────────

if ($SkipBuild) {
    Write-Step "Skipping build for TokenSaver.MCP"
} else {
    Write-Step "Publishing TokenSaver.MCP"

    $tokenSaverOutDir = Join-Path $PublishRoot "TokenSaver.MCP"
    New-Item -ItemType Directory -Force -Path $tokenSaverOutDir | Out-Null

    dotnet publish (Join-Path $RepoRoot "src\Host\TokenSaver.MCP\TokenSaver.MCP.csproj") `
        --configuration Release `
        --runtime       $Runtime `
        --self-contained true `
        --output        $tokenSaverOutDir

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for TokenSaver.MCP (exit $LASTEXITCODE)"
    }
    Write-Ok "Published to $tokenSaverOutDir"
}

# ── 3. Write appsettings.json in publish output ────────────────────────────────

Write-Step "Writing appsettings.json"

$tokenSaverExe      = Join-Path $PublishRoot "TokenSaver.MCP\TokenSaver.MCP.exe"
$appSettingsPath     = Join-Path $PublishRoot "TokenSaver.MCP\appsettings.json"

$appSettings = @{
    TokenSaver = @{
        ClaudeAutoStartProjectPath = $tokenSaverExe
        WorkspaceRoot              = $RepoRoot
    }
}

Write-JsonFile $appSettingsPath $appSettings
Write-Ok $appSettingsPath

# ── 4. Patch claude_desktop_config.json ────────────────────────────────────────

Write-Step "Patching claude_desktop_config.json"

$claudeConfigPath = Get-ClaudeConfigPath
Write-Host "    Path: $claudeConfigPath" -ForegroundColor DarkGray

$config = Read-JsoncFile $claudeConfigPath

if (-not $config.ContainsKey("mcpServers")) {
    $config["mcpServers"] = @{}
}

$existing = $config["mcpServers"]["token-saver"]
$desired  = @{ command = $tokenSaverExe }

if ($null -ne $existing -and
    $existing -is [hashtable] -and
    $existing["command"] -eq $tokenSaverExe)
{
    Write-Ok "Already configured — no changes needed"
}
else {
    $config["mcpServers"]["token-saver"] = $desired
    Write-JsonFile $claudeConfigPath $config
    Write-Ok "Registered token-saver -> $tokenSaverExe"
}

# ── Done ───────────────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "Setup complete." -ForegroundColor Green
Write-Host ""
Write-Host "  Published binaries : $PublishRoot"
Write-Host "  Claude config      : $claudeConfigPath"
Write-Host ""
Write-Host "Note: this only sets up TokenSaver.MCP. If you also need SimpleModelServer (the" -ForegroundColor Yellow
Write-Host "router TokenSaver.MCP talks to at http://localhost:11435), install it separately —" -ForegroundColor Yellow
Write-Host "see rsperry79/SimpleModelServer's own setup.ps1 / MSI installer." -ForegroundColor Yellow
Write-Host ""
Write-Host "Restart Claude Desktop to pick up the new MCP server." -ForegroundColor Yellow
