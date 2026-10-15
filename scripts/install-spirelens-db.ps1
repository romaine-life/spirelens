<#
.SYNOPSIS
Builds cmd/spirelens-db and installs it where SpireLens launches it from.

.DESCRIPTION
SpireLens starts "spirelens-db ingest" in the background on every Core load
and after every run end, but only if the tool exists at
%APPDATA%\SlayTheSpire2\SpireLens\tools\spirelens-db.exe (docs/run-database.md).
Re-run this script after changing the tool.

The new exe is built to a temp name first. An existing exe is renamed aside
before the new one moves in, because Windows lets a running exe be renamed
but not overwritten, so this works even while an import is running.

.PARAMETER Destination
The install path. Defaults to the location SpireLens looks in.

.PARAMETER Ingest
Run one ingest with the installed tool after installing it.
#>
param(
    [string] $Destination = (Join-Path $env:APPDATA "SlayTheSpire2\SpireLens\tools\spirelens-db.exe"),
    [switch] $Ingest
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if (-not (Get-Command go -ErrorAction SilentlyContinue)) {
    throw "Go is required (https://go.dev/dl/)."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$toolsDir = Split-Path -Parent $Destination
New-Item -ItemType Directory -Path $toolsDir -Force | Out-Null

$staging = "$Destination.new"
Push-Location $repoRoot
try {
    & go build -trimpath -o $staging ./cmd/spirelens-db
    if ($LASTEXITCODE -ne 0) { throw "go build failed." }
}
finally {
    Pop-Location
}

$aside = "$Destination.old"
if (Test-Path -LiteralPath $aside) {
    # Left over from an earlier install whose old exe was still running; try again.
    Remove-Item -LiteralPath $aside -Force -ErrorAction SilentlyContinue
}
if (Test-Path -LiteralPath $Destination) {
    if (Test-Path -LiteralPath $aside) {
        $aside = "$Destination.old-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))"
    }
    Move-Item -LiteralPath $Destination -Destination $aside -Force
}
Move-Item -LiteralPath $staging -Destination $Destination -Force
if (Test-Path -LiteralPath $aside) {
    Remove-Item -LiteralPath $aside -Force -ErrorAction SilentlyContinue
}

Write-Host "Installed $Destination"

if ($Ingest) {
    & $Destination ingest --quiet
    if ($LASTEXITCODE -ne 0) { throw "spirelens-db ingest failed." }
}
