#Requires -Version 5.1
<#
.SYNOPSIS
    Publishes and installs HyperVNetworkSwitcher (WinUI 3) to the current user's Programs folder.

.DESCRIPTION
    1. Stops any running instance of the app.
    2. Runs 'dotnet publish' to produce a self-contained WinUI 3 build (a folder of files,
       including the .pri resource index — NOT a single exe).
    3. Mirrors the publish folder to %LOCALAPPDATA%\Programs\HyperVNetworkSwitcher\,
       preserving any existing config.json.
    4. Removes the obsolete HKCU\Run startup value left by older versions.
    5. Optionally launches the app (UAC prompt — the app requires elevation for Hyper-V).

.PARAMETER Launch
    Launch the app immediately after installation.

.EXAMPLE
    .\Install.ps1
    .\Install.ps1 -Launch
#>
param([switch]$Launch)

$ErrorActionPreference = 'Stop'

$projectDir = $PSScriptRoot
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\HyperVNetworkSwitcher'
$publishDir = Join-Path $projectDir 'bin\Release\net10.0-windows10.0.26100.0\win-x64\publish'
$exeName    = 'HyperVNetworkSwitcher.exe'
$destExe    = Join-Path $installDir $exeName

Write-Host ''
Write-Host '================================================' -ForegroundColor Cyan
Write-Host '  HyperV Network Switcher  --  Install (WinUI 3)' -ForegroundColor Cyan
Write-Host '================================================' -ForegroundColor Cyan
Write-Host ''

# ── Step 1: Stop running instance ─────────────────────────────────────────────
$running = Get-Process -Name 'HyperVNetworkSwitcher' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "[1/5] Stopping running instance (PID $($running.Id))..." -ForegroundColor Yellow
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 800
} else {
    Write-Host '[1/5] No running instance found.' -ForegroundColor DarkGray
}

# ── Step 2: Publish ────────────────────────────────────────────────────────────
Write-Host '[2/5] Publishing self-contained WinUI 3 build...' -ForegroundColor Cyan
Push-Location $projectDir
try {
    dotnet publish HyperVNetworkSwitcher.csproj -c Release -r win-x64 --self-contained true --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
} finally {
    Pop-Location
}
if (-not (Test-Path (Join-Path $publishDir $exeName))) { throw "Published exe not found at: $publishDir" }
if (-not (Test-Path (Join-Path $publishDir 'HyperVNetworkSwitcher.pri'))) {
    throw "HyperVNetworkSwitcher.pri missing from publish output (WinUI would crash at startup)."
}
Write-Host '      Publish OK.' -ForegroundColor Green

# ── Step 3: Deploy (mirror folder, preserve config.json) ──────────────────────
Write-Host '[3/5] Deploying to install directory...' -ForegroundColor Cyan
if (-not (Test-Path $installDir)) { New-Item -ItemType Directory -Path $installDir -Force | Out-Null }

# /MIR mirrors the folder; /XF config.json keeps any user-edited config untouched.
robocopy $publishDir $installDir /MIR /XF config.json /NFL /NDL /NJH /NJS /NP | Out-Null
# robocopy uses 0-7 for success (1 = files copied). Only 8+ is a real failure.
if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE" }
$global:LASTEXITCODE = 0
Write-Host "      Mirrored $((Get-ChildItem $publishDir -File).Count)+ files -> $installDir" -ForegroundColor Green

# ── Step 4: Deploy config.json (only if absent) ───────────────────────────────
Write-Host '[4/5] Checking config.json...' -ForegroundColor Cyan
$destConfig = Join-Path $installDir 'config.json'
if (Test-Path $destConfig) {
    Write-Host '      config.json already exists -- keeping your existing config.' -ForegroundColor Yellow
} else {
    Copy-Item (Join-Path $publishDir 'config.json') $destConfig -Force
    Write-Host '      config.json -> install dir' -ForegroundColor Green
}

# ── Step 5: Remove obsolete HKCU\Run entry ────────────────────────────────────
Write-Host '[5/5] Removing obsolete startup entry (if any)...' -ForegroundColor Cyan
$runKey = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
if (Get-ItemProperty -Path $runKey -Name 'HyperVNetworkSwitcher' -ErrorAction SilentlyContinue) {
    Remove-ItemProperty -Path $runKey -Name 'HyperVNetworkSwitcher' -ErrorAction SilentlyContinue
    Write-Host '      Removed legacy HKCU\Run value.' -ForegroundColor Green
} else {
    Write-Host '      None found.' -ForegroundColor DarkGray
}

Write-Host ''
Write-Host 'Installation complete.' -ForegroundColor Green
Write-Host "  Executable  : $destExe"
Write-Host "  Config      : $destConfig"
Write-Host "  Logs        : $env:APPDATA\HyperVNetworkSwitcher\switcher.log"
Write-Host ''
Write-Host 'Note: the app requires elevation (UAC) to control Hyper-V.' -ForegroundColor DarkGray
Write-Host ''

if (-not $Launch) {
    $answer = Read-Host 'Launch HyperVNetworkSwitcher now? [y/N]'
    $Launch = $answer -match '^[Yy]$'
}
if ($Launch) {
    try {
        Write-Host 'Launching (UAC prompt will appear)...' -ForegroundColor Cyan
        Start-Process $destExe
        Write-Host 'Launched.' -ForegroundColor Green
    } catch {
        Write-Host "Could not launch automatically. Start it manually:`n  $destExe" -ForegroundColor Yellow
    }
}
