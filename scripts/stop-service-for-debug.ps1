#Requires -Version 7

<#
.SYNOPSIS
    Stops the TokenSaverMCP Windows service if it's running, elevating (one UAC prompt) only
    when actually needed. Invoked as a pre-build step for Debug builds of TokenSaver.MCP so an
    F5/Ctrl+F5 dev session doesn't lose the port-11436 bind race against the installed service.

.DESCRIPTION
    Mirrors the service-stop logic in run-dev-server.ps1 (step 1) but standalone, since this runs
    from an MSBuild <Exec> rather than the interactive dev-server script.
#>

$ServiceName = "TokenSaverMCP"
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

if (-not $svc -or $svc.Status -ne "Running") {
    exit 0
}

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if ($isAdmin) {
    Write-Host "    Stopping '$ServiceName' service (pre-build, for dev debug session)..." -ForegroundColor Yellow
    Stop-Service -Name $ServiceName -Force -ErrorAction Stop
    Start-Sleep 1
} else {
    Write-Host "    '$ServiceName' service is running — relaunching elevated to stop it (UAC prompt incoming)..." -ForegroundColor Yellow
    $elevated = Start-Process -FilePath "pwsh" -ArgumentList @("-NoProfile", "-Command", "Stop-Service -Name $ServiceName -Force") -Verb RunAs -Wait -PassThru
    if ($elevated.ExitCode -ne 0) {
        Write-Host "    WARN Failed to stop '$ServiceName' (exit $($elevated.ExitCode)) — the debug session may fail to bind its port." -ForegroundColor Yellow
    }
}
