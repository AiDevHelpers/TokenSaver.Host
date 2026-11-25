#Requires -Version 7

<#
.SYNOPSIS
    Stops the TokenSaverMCP Windows service (if running) and builds/runs TokenSaver.MCP directly
    out of build\staging — the dev loop, as opposed to updating the installed service copy.

.DESCRIPTION
    The "TokenSaverMCP" Windows service and a dev-loop `dotnet run` both bind the same port
    (TokenSaverConfig.ServerPort, default 11436) and would conflict if both tried to listen at
    once. This script stops the service first, then runs `dotnet run` against the MCP project —
    which builds into build\staging\TokenSaver.MCP\ (see Directory.Build.props; every project's
    bin/obj is redirected there, not the per-project folder) and launches the freshly-built
    binary directly from that output, in the foreground.

    Stopping a Windows service requires Administrator. If the service is actually running and
    this script wasn't started elevated, it relaunches itself elevated (one UAC prompt) in a new
    window and waits for that window to exit — the dev server itself then runs in that elevated
    window. If the service isn't running (or isn't installed), no elevation is needed and
    everything runs in the current, non-elevated window.

    This does NOT touch the installed copy at C:\Program Files\TokenSaver — for that, see
    rebuild-and-replace-mcp.ps1 / update-service.ps1 instead. Use this script for iterating on
    code; use those for pushing a build back to the always-on service.

    Press Ctrl+C to stop the dev server. The Windows service is left stopped afterward — restart
    it yourself (`Start-Service TokenSaverMCP`) or pass -RestartServiceOnExit to have this script
    do it automatically when the dev server exits.

    Console output (stdout+stderr) is teed to .tokensaver\dev-server.log (overwritten each run)
    in addition to the live window, so the running server's output can be tailed/grepped by
    another process or tool without reading the terminal's screen buffer directly.

    Before touching the service, this also checks whether some other process is already
    listening on the configured port (another dev server left running in a different terminal,
    or a client's own stdio-spawned instance that fell back to HTTP) and refuses to start unless
    that owner is the service itself (handled normally) or -Force is passed.

    This script also never touches the real config files. It maintains its own dev-prefixed
    copies under .tokensaver\ — dev-appsettings.json (seeded from
    src\Host\TokenSaver.MCP\appsettings.json) and dev-config.json (seeded from
    .tokensaver\config.json if present) — created once and left alone on later runs so local
    edits to them persist. dev-appsettings.json's TokenSaver:BehaviorConfigPath is pointed at
    dev-config.json, and the dev server is launched with `--server-config <path to
    dev-appsettings.json>`, so a dev session never reads or writes the real appsettings.json or
    .tokensaver\config.json.

    If the dev server exits with a non-zero code (build failure, port conflict, unhandled startup
    exception), this window pauses on "Press Enter to close this window..." instead of closing
    immediately — otherwise the error would flash and vanish, especially in the elevated-relaunch
    case where the dev server runs in a separate window this script only waits on. A normal
    Ctrl+C stop never hits this — PowerShell unwinds straight past it.

.PARAMETER Configuration
    dotnet build configuration. Defaults to "Debug" (dev loop) — pass "Release" to reproduce
    what the installed service actually runs.

.PARAMETER RestartServiceOnExit
    Restart the TokenSaverMCP service automatically once the dev server process exits (Ctrl+C or
    crash). Off by default — most dev sessions end with the developer explicitly deciding whether
    they want the service back.

.PARAMETER Force
    Proceed even if another process is already listening on the configured port (see step 0
    below). Off by default — a second dev server (or another client's stdio-spawned instance
    bound to the same HTTP port) silently losing the bind is a confusing failure mode, so this
    script refuses to start over an already-occupied port unless told to.

.EXAMPLE
    .\scripts\run-dev-server.ps1
    .\scripts\run-dev-server.ps1 -Configuration Release
    .\scripts\run-dev-server.ps1 -RestartServiceOnExit
    .\scripts\run-dev-server.ps1 -Force
#>

[CmdletBinding()]
param(
    [string] $Configuration = "Debug",
    [switch] $RestartServiceOnExit,
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot    = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$ServiceName = "TokenSaverMCP"
$CsProj      = Join-Path $RepoRoot "src\Host\TokenSaver.MCP\TokenSaver.MCP.csproj"
$DevAppSettingsPath = Join-Path $RepoRoot ".tokensaver\dev-appsettings.json"
$DevBehaviorConfigPath = Join-Path $RepoRoot ".tokensaver\dev-config.json"
$DevServerLogPath = Join-Path $RepoRoot ".tokensaver\dev-server.log"

function Write-Step([string]$msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)   { Write-Host "    OK  $msg" -ForegroundColor Green }

Write-Host ""
Write-Host "=== TokenSaver.MCP dev server ($Configuration) ===" -ForegroundColor Cyan

# ── 0. Refuse to start if another client is already bound to the port ──────────────
# The Windows service is handled separately in step 1 below (it gets stopped, not treated as a
# conflict) — this catches everything else: a dev server already running from another terminal
# or client, or a stdio-spawned instance (Claude Desktop/VS Code) that fell back to HTTP on this
# port. Racing two listeners on the same port is a confusing failure (one silently loses the
# bind, or Kestrel throws AddressInUseException deep into startup) — so this fails fast instead.

$ExpectedPort = 11436
if (Test-Path $DevAppSettingsPath) {
    try {
        $existingDevConfig = Get-Content $DevAppSettingsPath -Raw | ConvertFrom-Json
        if ($existingDevConfig.TokenSaver.ServerPort) {
            $ExpectedPort = [int]$existingDevConfig.TokenSaver.ServerPort
        }
    } catch {
        # Malformed dev-appsettings.json — fall through with the default port; the dotnet run
        # below will surface the real parse error.
    }
}

Write-Step "Checking whether port $ExpectedPort is already in use"

$portOwner = Get-NetTCPConnection -LocalPort $ExpectedPort -State Listen -ErrorAction SilentlyContinue |
    Select-Object -First 1

if ($portOwner) {
    $ownerProcess = Get-Process -Id $portOwner.OwningProcess -ErrorAction SilentlyContinue
    # If the SCM is running, that IS the prod server — full stop, no PID matching needed (and
    # PID matching via Win32_Service.ProcessId silently returns 0 without elevation, which would
    # misidentify the real service as a foreign conflict on every non-elevated run).
    $ownerIsThisService = (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue).Status -eq "Running"

    if ($ownerIsThisService) {
        Write-Ok "Port $ExpectedPort is held by the '$ServiceName' service — will be stopped in step 1"
    } elseif ($Force) {
        Write-Host "    WARN Port $ExpectedPort is already in use by PID $($portOwner.OwningProcess)$(if ($ownerProcess) { " ($($ownerProcess.ProcessName))" }) — continuing anyway (-Force)." -ForegroundColor Yellow
    } else {
        Write-Host ""
        Write-Host "    Port $ExpectedPort is already in use by PID $($portOwner.OwningProcess)$(if ($ownerProcess) { " ($($ownerProcess.ProcessName))" })." -ForegroundColor Red
        Write-Host "    This looks like another dev server or client already bound to this port." -ForegroundColor Red
        Write-Host "    Stop that process first, or re-run with -Force to proceed anyway." -ForegroundColor Red
        exit 1
    }
} else {
    Write-Ok "Port $ExpectedPort is free"
}

# ── 1. Stop the service, if running — otherwise the dev server can't bind the port ──

Write-Step "Checking for '$ServiceName' service"

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$wasServiceRunning = $false

if ($svc -and $svc.Status -eq "Running") {
    $isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        # Auto-elevate: relaunch this same script, with the same params, in a new elevated
        # window, then wait for that window to exit before returning control here. Only done
        # when actually needed (service running) — a non-elevated run with the service already
        # stopped never prompts for UAC.
        Write-Host "    Service is running — relaunching elevated to stop it (UAC prompt incoming)..." -ForegroundColor Yellow

        $arguments = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"", "-Configuration", "`"$Configuration`"")
        if ($RestartServiceOnExit) {
            $arguments += "-RestartServiceOnExit"
        }
        if ($Force) {
            $arguments += "-Force"
        }

        $elevated = Start-Process -FilePath "pwsh" -ArgumentList $arguments -WorkingDirectory $RepoRoot -Verb RunAs -Wait -PassThru
        exit $elevated.ExitCode
    }

    Write-Host "    Stopping service '$ServiceName'..." -ForegroundColor Yellow
    Stop-Service -Name $ServiceName -Force -ErrorAction Stop
    $wasServiceRunning = $true
    Start-Sleep 2
    Write-Ok "Service stopped"
} elseif ($svc) {
    Write-Ok "Service present but not running ($($svc.Status))"
} else {
    Write-Ok "Service not installed"
}

# ── 2. Seed dev-prefixed config copies, if they don't already exist ────────────────
# Keeps the dev loop off the real appsettings.json and .tokensaver/config.json entirely — created
# once, then left alone so local edits to them persist across runs.

Write-Step "Checking for dev config copies under .tokensaver\"

if (-not (Test-Path $DevAppSettingsPath)) {
    $sourceAppSettings = Join-Path $RepoRoot "src\Host\TokenSaver.MCP\appsettings.json"
    $devSettings = Get-Content $sourceAppSettings -Raw | ConvertFrom-Json
    if (-not $devSettings.TokenSaver) {
        $devSettings | Add-Member -NotePropertyName TokenSaver -NotePropertyValue ([pscustomobject]@{})
    }
    $devSettings.TokenSaver | Add-Member -NotePropertyName BehaviorConfigPath -NotePropertyValue $DevBehaviorConfigPath -Force
    $devSettings.TokenSaver | Add-Member -NotePropertyName VerboseLogging -NotePropertyValue $true -Force
    $devSettings | ConvertTo-Json -Depth 10 | Set-Content $DevAppSettingsPath
    Write-Ok "Created .tokensaver\dev-appsettings.json (seeded from appsettings.json)"
} else {
    Write-Ok ".tokensaver\dev-appsettings.json already exists — left as-is"
}

if (-not (Test-Path $DevBehaviorConfigPath)) {
    $sourceBehaviorConfig = Join-Path $RepoRoot ".tokensaver\config.json"
    if (Test-Path $sourceBehaviorConfig) {
        Copy-Item $sourceBehaviorConfig $DevBehaviorConfigPath
        Write-Ok "Created .tokensaver\dev-config.json (copied from config.json)"
    } else {
        Write-Ok ".tokensaver\config.json doesn't exist yet — dev-config.json will be created by the server on first run"
    }
} else {
    Write-Ok ".tokensaver\dev-config.json already exists — left as-is"
}

# ── 3. Build + run from build\staging (foreground; Ctrl+C to stop) ─────────────────
# TOKENSAVER_NO_SCM=1 forces this process to never register with the Windows Service Control
# Manager, regardless of what WindowsServiceHelpers.IsWindowsService()'s own detection would say
# in this process's launch context — the dev loop must always be a plain script/console launch.

Write-Step "Building and running (dotnet run, $Configuration)"
Write-Host "    Ctrl+C to stop." -ForegroundColor DarkGray
Write-Host "    Console output is also being teed to $DevServerLogPath" -ForegroundColor DarkGray

$devServerExitCode = 0
try {
    $env:TOKENSAVER_NO_SCM = "1"
    # Tee to a log file (overwritten each run) in addition to the live console — lets a tool or
    # another process tail the file for errors/stack traces without needing to read this window's
    # screen buffer directly. 2>&1 folds stderr into the same stream so both land in the log.
    dotnet run --project $CsProj --configuration $Configuration -- --server-config $DevAppSettingsPath 2>&1 |
        Tee-Object -FilePath $DevServerLogPath
    $devServerExitCode = $LASTEXITCODE
} catch {
    Write-Host ""
    Write-Host "    Dev server threw: $($_.Exception.Message)" -ForegroundColor Red
    $devServerExitCode = 1
} finally {
    Remove-Item Env:\TOKENSAVER_NO_SCM -ErrorAction SilentlyContinue
    # ── 4. Optionally hand the port back to the service ─────────────────────────────
    if ($RestartServiceOnExit -and $wasServiceRunning) {
        Write-Step "Restarting '$ServiceName' service"
        Start-Service -Name $ServiceName
        Start-Sleep 3

        $status = Get-Service -Name $ServiceName
        if ($status.Status -eq "Running") {
            Write-Ok "$ServiceName started successfully"
        } else {
            Write-Host "    WARN $ServiceName failed to start. Status: $($status.Status)" -ForegroundColor Yellow
        }
    } elseif ($wasServiceRunning) {
        Write-Host ""
        Write-Host "Note: '$ServiceName' was stopped for this dev session and was NOT restarted." -ForegroundColor Yellow
        Write-Host "Run 'Start-Service $ServiceName' (as Administrator) to bring it back." -ForegroundColor Yellow
    }
}

# ── 5. Wait on failure ───────────────────────────────────────────────────────────────
# A non-zero exit (build failure, port conflict deep in Kestrel, unhandled startup exception)
# would otherwise close this window immediately — especially bad for the elevated relaunch case
# (step 1 above), where the error would flash and vanish before anyone could read it. A clean
# Ctrl+C stop never reaches here (PowerShell unwinds straight out of the try/finally above on
# Ctrl+C and skips everything after it), so this only fires on an actual failure.
if ($devServerExitCode -ne 0) {
    Write-Host ""
    Write-Host "    Dev server exited with code $devServerExitCode." -ForegroundColor Red
    Write-Host "    Press Enter to close this window..." -ForegroundColor Yellow
    try { Read-Host | Out-Null } catch { }
    exit $devServerExitCode
}
