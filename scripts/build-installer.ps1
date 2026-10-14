<#
.SYNOPSIS
  Builds the UltraLogViewer Windows installer (Inno Setup).

.DESCRIPTION
  Publishes the app for win-x64, then compiles installer/UltraLogViewer.iss.
  The setup .exe lands in release/installer/.

  -App Avalonia (default): self-contained `dotnet publish` of dotnet/src/UltraLogViewer.Desktop.
  -App Electron:           `electron-builder --win dir` of the Electron app.

  Needs Inno Setup 6 (winget install JRSoftware.InnoSetup) plus the .NET 8 SDK
  or Node.js, depending on -App.

.EXAMPLE
  pwsh scripts/build-installer.ps1
  pwsh scripts/build-installer.ps1 -App Electron
  pwsh scripts/build-installer.ps1 -Version 1.0.0 -SkipBuild
#>
[CmdletBinding()]
param(
  [ValidateSet('Avalonia', 'Electron')]
  [string]$App = 'Avalonia',
  # Defaults to <Version> in the .csproj (Avalonia) or "version" in package.json (Electron).
  [string]$Version,
  # Reuse the existing publish output instead of building the app again.
  [switch]$SkipBuild,
  # Path to ISCC.exe; found automatically when omitted.
  [string]$Iscc = $env:ISCC,
  # Install Inno Setup with winget when it is missing (used by build-installer.cmd).
  [switch]$InstallTools
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'dotnet/src/UltraLogViewer.Desktop/UltraLogViewer.Desktop.csproj'

function Invoke-Native([string]$exe, [string[]]$arguments) {
  Write-Host "> $exe $($arguments -join ' ')" -ForegroundColor Cyan
  & $exe @arguments
  if ($LASTEXITCODE -ne 0) { throw "$exe exited with code $LASTEXITCODE" }
}

function Find-Iscc {
  if ($Iscc) { return $Iscc }
  $onPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue
  if ($onPath) { return $onPath.Source }
  $candidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
  )
  foreach ($c in $candidates) { if ($c -and (Test-Path $c)) { return $c } }
  if ($InstallTools -and (Get-Command winget -ErrorAction SilentlyContinue)) {
    Write-Host 'Inno Setup 6 not found; installing it with winget...' -ForegroundColor Yellow
    Invoke-Native 'winget' @('install', '--id', 'JRSoftware.InnoSetup', '-e', '--silent', '--accept-package-agreements', '--accept-source-agreements')
    foreach ($c in $candidates) { if ($c -and (Test-Path $c)) { return $c } }
  }
  throw 'Inno Setup 6 not found. Install it with "winget install JRSoftware.InnoSetup" or pass -Iscc <path to ISCC.exe>.'
}

function Assert-Tool([string]$name, [string]$hint) {
  if (-not (Get-Command $name -ErrorAction SilentlyContinue)) { throw "$name not found. $hint" }
}

if ($App -eq 'Avalonia') {
  $sourceDir = Join-Path $root 'release/avalonia-win-x64'
  if (-not $Version) { $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
  if (-not $SkipBuild) {
    Assert-Tool 'dotnet' 'Install the .NET 8 SDK: winget install Microsoft.DotNet.SDK.8'
    if (Test-Path $sourceDir) { Remove-Item $sourceDir -Recurse -Force }
    Invoke-Native 'dotnet' @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
      '-p:DebugType=none', "-p:Version=$Version", '-o', $sourceDir)
  }
} else {
  $sourceDir = Join-Path $root 'release/win-unpacked'
  if (-not $Version) { $Version = (Get-Content (Join-Path $root 'package.json') -Raw | ConvertFrom-Json).version }
  if (-not $SkipBuild) {
    Assert-Tool 'npm' 'Install Node.js: winget install OpenJS.NodeJS.LTS'
    Push-Location $root
    try { Invoke-Native 'npm' @('run', 'dist:win-dir') } finally { Pop-Location }
  }
}

if (-not $Version) { throw 'Could not determine the app version; pass -Version.' }
if (-not (Test-Path (Join-Path $sourceDir 'UltraLogViewer.exe'))) { throw "UltraLogViewer.exe not found in $sourceDir" }

$iss = Join-Path $root 'installer/UltraLogViewer.iss'
$outputDir = Join-Path $root 'release/installer'
Invoke-Native (Find-Iscc) @('/Q', "/DAppVersion=$Version", "/DAppSourceDir=$sourceDir",
  "/DAppFlavor=$($App.ToLowerInvariant())", "/DOutputDir=$outputDir", $iss)

Get-ChildItem $outputDir -Filter "UltraLogViewer-Setup-$Version-*.exe" | ForEach-Object { Write-Host "Built $($_.FullName)" -ForegroundColor Green }
