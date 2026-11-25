#Requires -Version 7
<#
.SYNOPSIS
    Rebuilds TokenSaverMCP (Release, self-contained) and updates the already-installed
    Windows service in place, without going through the WiX/MSI installer.

.DESCRIPTION
    dotnet build/publish only ever touches this repo's own bin/obj output — it never updates the
    separate installed copy the Windows service actually runs from (C:\Program Files\TokenSaver).
    After changing code that affects the service, running a plain `dotnet build` and expecting
    the running service to pick it up is a no-op: the service keeps running the old binary until
    it's explicitly stopped, the installed files replaced, and the service restarted. This script
    does that stop/publish/copy/start cycle.

    Must be run as Administrator — Stop-Service/Start-Service and writing into Program Files
    both require elevation.

    This script only updates TokenSaverMCP. If you also need to update SimpleModelServer, run
    the equivalent update-service.ps1 in the separate rsperry79/SimpleModelServer repo.

.PARAMETER Configuration
    dotnet build configuration. Defaults to "Release".

.EXAMPLE
    .\update-service.ps1
#>
[CmdletBinding()]
param(
    [string] $Configuration = "Release"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "This script must be run as Administrator!"
    exit 1
}

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

$ServiceName = "TokenSaverMCP"
$CsProj      = Join-Path $RepoRoot "src\Host\TokenSaver.MCP\TokenSaver.MCP.csproj"
$SourcePath  = Join-Path $RepoRoot "src\Host\TokenSaver.MCP\bin\$Configuration\net10.0\win-x64\publish"
$InstallDir  = "C:\Program Files\TokenSaver"

Write-Host ""
Write-Host "=== Updating $ServiceName ===" -ForegroundColor Cyan

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $svc) {
    throw "Service '$ServiceName' not found — is it installed?"
}

Write-Host "Stopping service '$ServiceName'..." -ForegroundColor Yellow
Stop-Service -Name $ServiceName -Force -ErrorAction Stop
Start-Sleep 2

Write-Host "Building $Configuration (self-contained)..." -ForegroundColor Yellow
dotnet publish $CsProj `
    -c $Configuration `
    -r win-x64 `
    --self-contained `
    -o $SourcePath `
    --force

if ($LASTEXITCODE -ne 0) {
    throw "Build failed for $ServiceName! Service left stopped — investigate before restarting manually."
}

Write-Host "Updating service files..." -ForegroundColor Yellow
Copy-Item "$SourcePath\*" -Destination $InstallDir -Force -Recurse

Write-Host "Starting service..." -ForegroundColor Yellow
Start-Service -Name $ServiceName
Start-Sleep 3

$status = Get-Service -Name $ServiceName
if ($status.Status -eq "Running") {
    Write-Host "OK  $ServiceName started successfully." -ForegroundColor Green
} else {
    throw "$ServiceName failed to start. Status: $($status.Status)"
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
