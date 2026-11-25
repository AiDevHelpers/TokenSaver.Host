#Requires -Version 7

<#
.SYNOPSIS
    Rebuilds TokenSaver.MCP (Release, self-contained) and replaces whatever copy is currently
    active at C:\Program Files\TokenSaver — Windows service and/or stdio-spawned process.

.DESCRIPTION
    TokenSaver.MCP.exe can be running two different ways at once:
      1. As the "TokenSaverMCP" Windows service (HTTP-only, always-on).
      2. As a stdio-spawned child process launched per-session by Claude Desktop / VS Code /
         another MCP client, pointed at the same C:\Program Files\TokenSaver\TokenSaver.MCP.exe.
    Either way, the exe is locked while running, so a plain publish-and-copy fails or silently
    leaves the old binary in place. This script:
      1. Stops the "TokenSaverMCP" service, if installed and running.
      2. Kills any other running TokenSaver.MCP.exe processes (stdio-spawned instances) so the
         file isn't locked.
      3. dotnet publish (Release, self-contained, win-x64) into build\staging\TokenSaver.MCP\bin\
         (see Directory.Build.props — every project's bin/obj is redirected there, not the
         per-project folder a plain `dotnet publish` output path would suggest).
      4. Copies the published output over C:\Program Files\TokenSaver.
      5. Restarts the service if it was running before step 1. This also runs when steps 3 or 4
         fail, so a failed publish or copy never leaves the service stopped.

    Stdio-spawned instances are NOT restarted here — there's no persistent one to restart. The
    next session an AI client starts will spawn the new binary automatically; an already-open
    session keeps running the (now-dead) old process until the client reconnects/restarts.

    Must be run as Administrator — Stop-Service/Start-Service and writing into Program Files
    both require elevation. The elevated run writes a transcript to build\logs, because the
    elevated window closes when the script ends and its output is otherwise lost.

.PARAMETER Configuration
    dotnet build configuration. Defaults to "Release".

.PARAMETER InstallDir
    Where the active copy lives. Defaults to "C:\Program Files\TokenSaver".

.EXAMPLE
    .\scripts\rebuild-and-replace-mcp.ps1
#>

[CmdletBinding()]
param(
    [string] $Configuration = "Release",
    [string] $InstallDir    = "C:\Program Files\TokenSaver"
)

# ── Auto-elevate if not already running as Administrator ─────────────────────────
if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    # Relaunch as Administrator with the same arguments. Each argument is its own array element;
    # a single "-ExecutionPolicy Bypass" string is passed as one token and is not parsed as two.
    $arguments = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", $PSCommandPath
    )

    # Forward the script parameters that were passed, as separate name/value tokens
    foreach ($key in $PSBoundParameters.Keys) {
        $arguments += "-$key"
        $arguments += [string]$PSBoundParameters[$key]
    }

    # Return at once: the elevated copy runs in its own window and logs to build\logs.
    $pwshPath = (Get-Process -Id $PID).Path
    Start-Process -FilePath $pwshPath -ArgumentList $arguments -Verb RunAs
    Write-Host "Elevated rebuild started in a separate window. Its log goes to build\logs\rebuild-and-replace-mcp-<timestamp>.log."
    exit 0
}

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot    = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$ServiceName = "TokenSaverMCP"
$CsProj      = Join-Path $RepoRoot "src\Host\TokenSaver.MCP\TokenSaver.MCP.csproj"
$PublishDir  = Join-Path $RepoRoot "build\staging\TokenSaver.MCP\bin\$Configuration\net10.0\win-x64\publish"
$ExeName     = "TokenSaver.MCP.exe"
$LogDir      = Join-Path $RepoRoot "build\logs"
$LogPath     = Join-Path $LogDir ("rebuild-and-replace-mcp-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))

function Write-Step([string]$msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)   { Write-Host "    OK  $msg" -ForegroundColor Green }
function Write-Warn([string]$msg) { Write-Host "    WARN $msg" -ForegroundColor Yellow }

New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
Start-Transcript -Path $LogPath -Force | Out-Null

$wasServiceRunning = $false
$exitCode = 0

try {
    Write-Host ""
    Write-Host "=== Rebuilding and replacing $ExeName ($InstallDir) ===" -ForegroundColor Cyan
    Write-Host "    Log: $LogPath"

    # ── 1. Stop the service, if present ─────────────────────────────────────────

    Write-Step "Checking for '$ServiceName' service"

    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

    if ($svc) {
        if ($svc.Status -eq "Running") {
            Write-Host "    Stopping service '$ServiceName'..." -ForegroundColor Yellow
            Stop-Service -Name $ServiceName -Force -ErrorAction Stop
            $wasServiceRunning = $true
            Start-Sleep 2
            Write-Ok "Service stopped"
        } else {
            Write-Ok "Service present but not running ($($svc.Status))"
        }
    } else {
        Write-Ok "Service not installed — stdio-only setup assumed"
    }

    # ── 2. Kill any other running instances so the exe isn't locked ────────────

    Write-Step "Checking for running $ExeName processes"

    $procs = Get-Process -Name "TokenSaver.MCP" -ErrorAction SilentlyContinue
    if ($procs) {
        foreach ($p in $procs) {
            Write-Host "    Stopping PID $($p.Id)..." -ForegroundColor Yellow
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        }
        Start-Sleep 1
        Write-Ok "Stopped $(@($procs).Count) running instance(s)"
    } else {
        Write-Ok "No running instances found"
    }

    # ── 3. Publish ───────────────────────────────────────────────────────────────

    Write-Step "Publishing $Configuration (self-contained, win-x64)"

    dotnet publish $CsProj `
        -c $Configuration `
        -r win-x64 `
        --self-contained true `
        -o $PublishDir `
        --force

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed (exit $LASTEXITCODE). Active copy at $InstallDir was not touched."
    }
    Write-Ok "Published to $PublishDir"

    # ── 4. Replace the active copy ──────────────────────────────────────────────

    Write-Step "Replacing active copy at $InstallDir"

    if (-not (Test-Path $InstallDir)) {
        New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    }

    Copy-Item "$PublishDir\*" -Destination $InstallDir -Force -Recurse
    Write-Ok "Copied published output into $InstallDir"
}
catch {
    $exitCode = 1
    Write-Host ""
    Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
}
finally {
    # ── 5. Restart the service if it was running, even after a failure above ────

    # Start the service after a successful deploy even if it was stopped before, so the
    # installed copy is always the one running. After a failed deploy, restart only if it was up.
    if ($svc -and ($wasServiceRunning -or $exitCode -eq 0)) {
        Write-Step "Starting '$ServiceName' service"
        try {
            Start-Service -Name $ServiceName
            Start-Sleep 3

            $status = Get-Service -Name $ServiceName
            if ($status.Status -eq "Running") {
                Write-Ok "$ServiceName started successfully"
            } else {
                Write-Warn "$ServiceName status after start: $($status.Status)"
                $exitCode = 1
            }
        }
        catch {
            Write-Host "    Could not restart '$ServiceName': $($_.Exception.Message)" -ForegroundColor Red
            $exitCode = 1
        }
    }
}

Write-Host ""
if ($exitCode -eq 0) {
    Write-Host "Done." -ForegroundColor Green
    Write-Host "Any open Claude Desktop / VS Code session using the stdio transport is still running" -ForegroundColor Yellow
    Write-Host "the old binary until that client restarts its MCP connection." -ForegroundColor Yellow
} else {
    Write-Host "Finished with errors. See $LogPath" -ForegroundColor Red
}
Write-Host ""

Stop-Transcript | Out-Null
exit $exitCode
