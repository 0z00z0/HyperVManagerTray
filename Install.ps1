#Requires -Version 5.1
<#
.SYNOPSIS
    Publishes and installs HyperVNetworkSwitcher to the current user's Programs folder.

.DESCRIPTION
    1. Stops any running instance of the app.
    2. Runs 'dotnet publish' to produce a self-contained single-file executable.
    3. Copies the executable to %LOCALAPPDATA%\Programs\HyperVNetworkSwitcher\.
    4. Copies config.json only if one does not already exist at the destination
       (preserves any configuration you have already edited).
    5. Optionally launches the app with elevation.

.PARAMETER Launch
    If specified, launches the app immediately after installation (UAC prompt will appear).

.EXAMPLE
    .\Install.ps1
    .\Install.ps1 -Launch
#>
param(
    [switch]$Launch
)

$ErrorActionPreference = 'Stop'

# ── Paths ─────────────────────────────────────────────────────────────────────
$projectDir = $PSScriptRoot
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\HyperVNetworkSwitcher'
$publishDir = Join-Path $projectDir 'bin\Release\net10.0-windows\win-x64\publish'
$exeName    = 'HyperVNetworkSwitcher.exe'
$configName = 'config.json'
$destExe    = Join-Path $installDir $exeName
$destConfig = Join-Path $installDir $configName

Write-Host ''
Write-Host '================================================' -ForegroundColor Cyan
Write-Host '  HyperV Network Switcher  --  Install'          -ForegroundColor Cyan
Write-Host '================================================' -ForegroundColor Cyan
Write-Host ''

# ── Step 1: Stop running instance ─────────────────────────────────────────────
$running = Get-Process -Name 'HyperVNetworkSwitcher' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "[1/4] Stopping running instance (PID $($running.Id))..." -ForegroundColor Yellow
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 800
} else {
    Write-Host '[1/4] No running instance found.' -ForegroundColor DarkGray
}

# ── Step 2: Publish ────────────────────────────────────────────────────────────
Write-Host '[2/4] Publishing self-contained executable...' -ForegroundColor Cyan
Push-Location $projectDir
try {
    dotnet publish -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:DebugType=none --nologo -v quiet
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }
} finally {
    Pop-Location
}
Write-Host '      Publish OK.' -ForegroundColor Green

# ── Step 3: Deploy executable ──────────────────────────────────────────────────
Write-Host '[3/4] Deploying to install directory...' -ForegroundColor Cyan

if (-not (Test-Path $installDir)) {
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
}

$srcExe = Join-Path $publishDir $exeName
if (-not (Test-Path $srcExe)) {
    throw "Published executable not found at: $srcExe"
}
Copy-Item $srcExe $destExe -Force
Write-Host "      $exeName -> $installDir" -ForegroundColor Green

# ── Step 4: Deploy config.json (preserve existing) ────────────────────────────
Write-Host '[4/4] Checking config.json...' -ForegroundColor Cyan

if (Test-Path $destConfig) {
    Write-Host '      config.json already exists -- keeping your existing config.' -ForegroundColor Yellow
} else {
    $srcConfig = Join-Path $projectDir $configName
    if (Test-Path $srcConfig) {
        Copy-Item $srcConfig $destConfig -Force
        Write-Host "      config.json -> $installDir" -ForegroundColor Green
    } else {
        Write-Warning "config.json not found in project directory. Create one at: $destConfig"
    }
}

# ── Summary ────────────────────────────────────────────────────────────────────
Write-Host ''
Write-Host 'Installation complete.' -ForegroundColor Green
Write-Host ''
Write-Host "  Executable  : $destExe"
Write-Host "  Config      : $destConfig"
Write-Host "  Logs        : $env:APPDATA\HyperVNetworkSwitcher\switcher.log"
Write-Host ''
Write-Host 'Note: the app requires elevation (UAC) to control Hyper-V switches.' -ForegroundColor DarkGray
Write-Host ''

# ── Launch ─────────────────────────────────────────────────────────────────────
if (-not $Launch) {
    $answer = Read-Host 'Launch HyperVNetworkSwitcher now? [y/N]'
    $Launch = $answer -match '^[Yy]$'
}

if ($Launch) {
    # The app manifest already requests requireAdministrator, so Windows will
    # show a UAC prompt automatically -- no need to use -Verb RunAs here.
    try {
        Write-Host 'Launching (UAC prompt will appear)...' -ForegroundColor Cyan
        Start-Process $destExe
        Write-Host 'Launched.' -ForegroundColor Green
    } catch {
        Write-Host ''
        Write-Host 'Could not launch automatically. Start it manually:' -ForegroundColor Yellow
        Write-Host "  $destExe" -ForegroundColor White
    }
}
