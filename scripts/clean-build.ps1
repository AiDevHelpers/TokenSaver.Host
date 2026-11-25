<#
.SYNOPSIS
    Removes all bin/ and obj/ (and stray build/staging) output folders from the repo.

.DESCRIPTION
    Directory.Build.props now redirects every ordinary .NET project's bin/obj into
    build\staging\<ProjectName>\, but old per-project bin/obj folders can still be lying
    around from before that change (or from IDE builds run before this script existed).
    This script deletes:
      - every bin/ and obj/ folder found anywhere under the repo (excluding node_modules,
        since TokenSaver.VsCodeExt's node_modules\...\bin entries are npm package binaries,
        not build output),
      - build\staging\ and build\dist\ at the repo root, for a fully clean slate, and
      - any stray "build" folder found under src\ (e.g. src\Host\TokenSaver.MCP\build) —
        these are leftovers from a manual `dotnet publish -o build\staging` (or similar) run
        with a relative -o path from inside a project directory instead of the repo root, not
        anything the normal build/publish process creates.

    Run this from a normal Windows shell (PowerShell) where you have full file permissions —
    it won't work from a sandboxed/remote session that only has restricted access to the repo.

.PARAMETER WhatIf
    Preview what would be deleted without actually deleting anything.

.EXAMPLE
    .\scripts\clean-build.ps1

.EXAMPLE
    .\scripts\clean-build.ps1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

Write-Host "Cleaning build output under $repoRoot ..." -ForegroundColor Cyan

# 1. Per-project bin/obj folders anywhere under the repo, excluding node_modules.
$binObjDirs = Get-ChildItem -Path $repoRoot -Recurse -Directory -Force -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Name -in @('bin', 'obj') -and
        $_.FullName -notmatch '\\node_modules\\'
    }

foreach ($dir in $binObjDirs) {
    if ($PSCmdlet.ShouldProcess($dir.FullName, "Remove directory")) {
        Write-Host "  Removing $($dir.FullName)"
        Remove-Item -LiteralPath $dir.FullName -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# 2. The shared staging/dist folders themselves.
foreach ($name in @('staging', 'dist')) {
    $path = Join-Path $repoRoot "build\$name"
    if (Test-Path $path) {
        if ($PSCmdlet.ShouldProcess($path, "Remove directory")) {
            Write-Host "  Removing $path"
            Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

# 3. Stray "build" folders under src\ — e.g. src\Host\TokenSaver.MCP\build — left behind by a
#    manual `dotnet publish -o build\staging` (or similar) run with a relative path from inside
#    a project directory instead of the repo root. Not created by the normal build/publish flow.
$strayBuildDirs = Get-ChildItem -Path (Join-Path $repoRoot 'src') -Recurse -Directory -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -eq 'build' -and $_.FullName -notmatch '\\node_modules\\' }

foreach ($dir in $strayBuildDirs) {
    if ($PSCmdlet.ShouldProcess($dir.FullName, "Remove directory")) {
        Write-Host "  Removing stray $($dir.FullName)"
        Remove-Item -LiteralPath $dir.FullName -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "Done." -ForegroundColor Green
