<#
.SYNOPSIS
    Builds the per-user Inno Setup installer for Hyper-V Manager Tray.

.DESCRIPTION
    1. Ensures Assets\app.ico exists (generates it if absent via Generate-AppIcon.ps1).
    2. Publishes the app fully self-contained (win-x64, Windows App SDK bundled, no trimming  - 
       trimming breaks WinUI 3).
    3. Compiles installer\HyperVManagerTray.iss with Inno Setup (ISCC.exe).
    4. Moves the signed setup to the local installer shelf — skipped in CI, which
       signs and publishes from installer\Output.

    Output: HyperVManagerTray-Setup-{version}.exe (per-user, no admin to install;
    the app elevates itself at runtime).

    Requires Inno Setup (ISCC). If missing, install it once:
        winget install JRSoftware.InnoSetup

.EXAMPLE
    .\build-installer.ps1                  # auto-bumps patch (e.g. 2.1.2 -> 2.1.3)
    .\build-installer.ps1 -Version 2.2.0   # explicit override
#>
[CmdletBinding()]
param(
    [string] $Version = ""   # empty = auto-bump patch from .csproj
)

$ErrorActionPreference = "Stop"

$installerDir = $PSScriptRoot
$root         = Split-Path $installerDir -Parent
$proj         = Join-Path $root "HyperVManagerTray.csproj"
$publishDir   = Join-Path $root "publish"
$iss          = Join-Path $installerDir "HyperVManagerTray.iss"
$shelfDir     = "C:\Users\EspenLaget\Nextcloud\Projects\Installers"   # local dev shelf; see step 6

# -- 0. Resolve / bump version ------------------------------------------------
$projContent = Get-Content $proj -Raw
$vMatch      = [regex]::Match($projContent, '<Version>(\d+\.\d+\.\d+)</Version>')
if (-not $vMatch.Success) { throw "Cannot find <Version>x.y.z</Version> in $proj" }
$currentVersion = $vMatch.Groups[1].Value

if ([string]::IsNullOrEmpty($Version)) {
    # Auto-bump: increment patch component
    $v       = [System.Version]$currentVersion
    $Version = "{0}.{1}.{2}" -f $v.Major, $v.Minor, ($v.Build + 1)
    Write-Host "==> Auto-bumping version:  $currentVersion  ->  $Version" -ForegroundColor Cyan
} else {
    Write-Host "==> Using explicit version: $Version" -ForegroundColor Cyan
}

# Write the new version back to the .csproj (idempotent if already correct)
if ($currentVersion -ne $Version) {
    ($projContent -replace "<Version>$currentVersion</Version>", "<Version>$Version</Version>") |
        Set-Content $proj -NoNewline
    Write-Host "    Updated HyperVManagerTray.csproj: $currentVersion -> $Version" -ForegroundColor DarkGray
}

# -- 1. Ensure Assets\app.ico exists -----------------------------------------
$appIco = Join-Path $root "Assets\app.ico"
if (-not (Test-Path $appIco)) {
    Write-Host "==> Generating Assets\app.ico ..." -ForegroundColor Cyan
    & powershell -ExecutionPolicy Bypass -File (Join-Path $installerDir "Generate-AppIcon.ps1") -ProjectRoot $root
    if ($LASTEXITCODE -ne 0) { throw "Generate-AppIcon.ps1 failed ($LASTEXITCODE)." }
}

# -- 2. Publish the app (framework-dependent, Windows App SDK bundled) --------
# Uses an installed .NET 10 Desktop Runtime instead of bundling it, keeping the
# installer small. Windows App SDK is still self-contained (uncommon dependency).
# H.NotifyIcon.WinUI and System.Drawing.Common ship as DLLs next to the exe.
# WindowsAppSDKSelfContained is declared in HyperVManagerTray.csproj, never passed here: an MSBuild
# property given on the command line is global and reaches every project in the graph, and a shared
# class library errors when it receives it.
Write-Host "==> Publishing app (framework-dependent win-x64, Windows App SDK bundled)..." -ForegroundColor Cyan
Write-Host "    (ReadyToRun compilation may take several minutes  -  this is normal)" -ForegroundColor DarkGray
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $proj `
    -c Release -r win-x64 --self-contained false `
    -p:PublishTrimmed=false -p:PublishReadyToRun=true `
    -o $publishDir -v minimal
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

if (-not (Test-Path (Join-Path $publishDir "HyperVManagerTray.pri"))) {
    throw "HyperVManagerTray.pri missing from publish output - WinUI would crash at startup (0xC000027B)."
}

# -- 2b. Sign the published exe -----------------------------------------------
# dotnet publish creates a fresh apphost in the publish folder  -  a separate binary
# from the bin\ build output that SignOutput already signed. Sign this copy so the
# installed exe is not flagged as Unsigned by security tools.
$publishedExe = Join-Path $publishDir "HyperVManagerTray.exe"
if (Test-Path $publishedExe) {
    Write-Host "==> Signing published exe..." -ForegroundColor Cyan
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "scripts\sign.ps1") -Path $publishedExe
}

# -- 3. Locate Inno Setup compiler --------------------------------------------
$iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    foreach ($p in @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",     # winget per-user install
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe")) {
        if (Test-Path $p) { $iscc = $p; break }
    }
}
if (-not $iscc) {
    throw "Inno Setup (ISCC.exe) not found. Install it once with:  winget install JRSoftware.InnoSetup"
}

# -- 4. Compile the installer -------------------------------------------------
# Remove any previous installers from Output/ so only the current build ships.
$outputDir = Join-Path $installerDir "Output"
if (Test-Path $outputDir) {
    $old = Get-ChildItem $outputDir -Filter "HyperVManagerTray-Setup-*.exe" -File -ErrorAction SilentlyContinue
    foreach ($f in $old) {
        # Use cmd /c del to bypass OneDrive cloud-placeholder reparse points that
        # PowerShell's Remove-Item cannot delete (access denied on the placeholder).
        cmd /c "del /F `"$($f.FullName)`"" 2>$null
        if (Test-Path $f.FullName) {
            Write-Warning "Could not remove old installer (file in use?): $($f.Name)"
        } else {
            Write-Host "    Removed old installer: $($f.Name)" -ForegroundColor DarkGray
        }
    }
}
Write-Host "==> Compiling installer with $iscc ..." -ForegroundColor Cyan
& $iscc "/DAppVersion=$Version" "/DPublishDir=$publishDir" $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)." }

$setup = Join-Path $installerDir "Output\HyperVManagerTray-Setup-$Version.exe"

# -- 5. Sign the installer exe ------------------------------------------------
# Sign before computing the SHA so the printed hash matches the distributed file.
if (Test-Path $setup) {
    Write-Host "==> Signing installer..." -ForegroundColor Cyan
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "scripts\sign.ps1") -Path $setup
    # Non-fatal: sign.ps1 prints a warning and exits 0 if the cert is absent.
}

# -- 6. Move the signed installer to the local installer shelf ----------------
# Local dev only: CI signs from and attaches installer\Output, so the file must stay
# put there. Guarded twice — GITHUB_ACTIONS is the explicit check, the folder test also
# covers any other machine that has no shelf.
if (-not $env:GITHUB_ACTIONS -and (Test-Path $shelfDir) -and (Test-Path $setup)) {
    # Only this app's setups — the shelf is shared with the other studio apps.
    Get-ChildItem $shelfDir -Filter "HyperVManagerTray-Setup-*.exe" -File -ErrorAction SilentlyContinue |
        Where-Object Name -ne (Split-Path $setup -Leaf) |
        Remove-Item -Force -ErrorAction SilentlyContinue

    $shelved  = Join-Path $shelfDir (Split-Path $setup -Leaf)
    $expected = (Get-FileHash $setup -Algorithm SHA256).Hash
    Move-Item $setup $shelved -Force

    # A sync client has silently rolled back a fresh binary write here before, so the
    # move is only believed once the bytes at the destination hash the same.
    $actual = (Get-FileHash $shelved -Algorithm SHA256).Hash
    if ($actual -ne $expected) {
        throw "Moved installer does not match: expected $expected, got $actual at $shelved."
    }
    $setup = $shelved
}

Write-Host ""
Write-Host "Done -> $setup" -ForegroundColor Green
if (Test-Path $setup) {
    $sha = (Get-FileHash $setup -Algorithm SHA256).Hash
    Write-Host "SHA256: $sha"
}
