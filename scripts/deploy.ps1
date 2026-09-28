<#
.SYNOPSIS
    Dev loop for SysPulse: stop the running extension, build, and register the loose MSIX layout.
.DESCRIPTION
    Requires Developer Mode. After it finishes, run "Reload" in Command Palette
    (or restart Command Palette) so the host picks up the new build.
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')] [string] $Platform = 'x64',
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Debug',
    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'SysPulse\SysPulse\SysPulse.csproj'

# The host keeps the out-of-proc COM server alive; it must be stopped before re-registering.
Get-Process -Name 'SysPulse' -ErrorAction SilentlyContinue | Stop-Process -Force

if (-not $NoBuild) {
    dotnet build $project -c $Configuration -p:Platform=$Platform -v:minimal
    if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }
}

$rid = if ($Platform -eq 'x64') { 'win-x64' } else { 'win-arm64' }
$manifest = Get-ChildItem (Join-Path $root "SysPulse\SysPulse\bin\$Platform\$Configuration") -Recurse -Filter AppxManifest.xml |
    Where-Object { $_.DirectoryName -like "*\$rid" } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $manifest) { throw "AppxManifest.xml not found for $Platform/$Configuration" }

Add-AppxPackage -Register $manifest.FullName -ForceUpdateFromAnyVersion
Write-Host "Registered $($manifest.FullName)"
Write-Host 'Now reload extensions in Command Palette.'
