<#
.SYNOPSIS
    Read-only diagnostic for the adapter-rename feature (issue #15): did the FriendlyName
    actually reach the registry, and is Windows surfacing it?

.DESCRIPTION
    Mirrors exactly what the app does, so a discrepancy here is a discrepancy in the app:

      1. Reads the network Class key and builds the NetCfgInstanceId -> DeviceInstanceID map
         (same as AdapterRenamer.ReadClassAdapterEntries).
      2. Resolves each live NIC's InterfaceGuid to a single DeviceInstanceID
         (same as AdapterNameRules.ResolveDeviceInstanceId - 0 or >1 matches = the app aborts).
      3. Reads HKLM\SYSTEM\CurrentControlSet\Enum\<id>\FriendlyName - the one value rename writes.
      4. Compares it against the description Windows actually surfaces (NDIS InterfaceDescription)
         to separate "never written" from "written but not surfaced".
      5. Prints what the app *believes* it applied, from config.json's adapterNames.
      6. Tails the "Rename flow" lines from ui.log - the app's own account of the last attempt.

    CHANGES NOTHING. Every registry access is a read.

    ASCII-only by design: Windows PowerShell 5.1 reads unmarked .ps1 files as ANSI, so any
    non-ASCII character here (em dash, box drawing) would corrupt and break parsing.

.NOTES
    Run elevated - the app writes this key elevated, so read it the same way.
#>
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    # Also tee everything to a file, so the result can be read without copy-pasting the console.
    [string] $OutFile = (Join-Path $env:TEMP 'HyperVManagerTray-rename-diagnostic.txt')
)

$ErrorActionPreference = 'Stop'

if ($OutFile) {
    try { Start-Transcript -Path $OutFile -Force | Out-Null } catch { }
}

$classKeyPath = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}'
$enumPrefix   = 'HKLM:\SYSTEM\CurrentControlSet\Enum\'
$uiLogPath    = Join-Path $env:APPDATA 'HyperVManagerTray\ui.log'

# config.json lives NEXT TO THE EXE (ConfigManager.GetConfigPath), not in %APPDATA% - the logs and
# the config deliberately live in different places. Probe the install locations in order.
$configPath = $null
foreach ($candidate in @(
    (Join-Path $env:LOCALAPPDATA 'Programs\HyperVManagerTray\config.json'),
    (Join-Path ${env:ProgramFiles} 'HyperVManagerTray\config.json'),
    (Join-Path ${env:ProgramFiles(x86)} 'HyperVManagerTray\config.json'))) {
    if ($candidate -and (Test-Path $candidate)) { $configPath = $candidate; break }
}
if (-not $configPath) {
    $configPath = Join-Path $env:LOCALAPPDATA 'Programs\HyperVManagerTray\config.json'
}

function Write-Head([string] $text) {
    Write-Host ''
    Write-Host ("== {0} {1}" -f $text, ('=' * [Math]::Max(0, 70 - $text.Length))) -ForegroundColor Cyan
}

Write-Host ''
Write-Host ("PowerShell {0} | {1}" -f $PSVersionTable.PSVersion, (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))

# -- 1. Class key: NetCfgInstanceId -> DeviceInstanceID -----------------------
Write-Head 'Class-key adapter entries (NetCfgInstanceId -> DeviceInstanceID)'

$entries = @()
foreach ($sub in (Get-ChildItem $classKeyPath -ErrorAction SilentlyContinue)) {
    try {
        $p = Get-ItemProperty $sub.PSPath -ErrorAction Stop
        if ($p.NetCfgInstanceId -and $p.DeviceInstanceID) {
            $entries += [pscustomobject]@{
                NetCfgInstanceId = $p.NetCfgInstanceId
                DeviceInstanceID = $p.DeviceInstanceID
                DriverDesc       = $p.DriverDesc
            }
        }
    } catch { }
}
Write-Host ("    {0} adapter entries found in the Class key." -f $entries.Count)

# -- 2/3/4. Per-NIC resolution + FriendlyName read ----------------------------
Write-Head 'Live NICs: resolution + registry FriendlyName vs surfaced description'

$report = @()
foreach ($nic in (Get-NetAdapter -ErrorAction SilentlyContinue | Sort-Object Name)) {
    $guid = $nic.InterfaceGuid
    # NOTE: not $matches - that is a PowerShell automatic variable.
    $classHits = @($entries | Where-Object { $_.NetCfgInstanceId -eq $guid })

    $resolution = switch ($classHits.Count) {
        0       { 'NO MATCH - app would abort' }
        1       { 'ok' }
        default { "AMBIGUOUS ({0} matches) - app would abort" -f $classHits.Count }
    }

    $devInst = $null
    if ($classHits.Count -eq 1) { $devInst = $classHits[0].DeviceInstanceID }

    $friendly = $null
    $friendlyPresent = $false
    $deviceDesc = $null

    if ($devInst) {
        $enumKey = $enumPrefix + $devInst
        try {
            $ep = Get-ItemProperty $enumKey -ErrorAction Stop
            $deviceDesc = $ep.DeviceDesc
            if ($null -ne $ep.FriendlyName) {
                $friendly = $ep.FriendlyName
                $friendlyPresent = $true
            }
        } catch {
            $resolution = "ENUM KEY UNREADABLE: $enumKey"
        }
    }

    # The verdict the app cannot tell you: written-but-not-surfaced vs never-written.
    if (-not $devInst) {
        $verdict = 'device not resolved'
    } elseif (-not $friendlyPresent) {
        $verdict = 'NO FriendlyName on disk - rename never landed'
    } elseif ($friendly -eq $nic.InterfaceDescription) {
        $verdict = 'FriendlyName present AND surfaced'
    } else {
        $verdict = 'FriendlyName present but NOT surfaced - needs adapter restart/reboot'
    }

    $shown = '<absent>'
    if ($friendlyPresent) { $shown = $friendly }

    $report += [pscustomobject]@{
        Name                 = $nic.Name
        InterfaceDescription = $nic.InterfaceDescription
        InterfaceGuid        = $guid
        Resolution           = $resolution
        DeviceInstanceID     = $devInst
        DeviceDesc           = $deviceDesc
        FriendlyName         = $shown
        Verdict              = $verdict
    }
}

foreach ($r in $report) {
    $colour = 'DarkGray'
    if     ($r.Verdict -like '*AND surfaced*') { $colour = 'Green'  }
    elseif ($r.Verdict -like '*NOT surfaced*') { $colour = 'Yellow' }
    elseif ($r.Verdict -like '*never landed*') { $colour = 'Red'    }

    Write-Host ''
    Write-Host ("  {0}" -f $r.Name) -ForegroundColor White
    Write-Host ("    surfaced description : {0}" -f $r.InterfaceDescription)
    Write-Host ("    registry FriendlyName: {0}" -f $r.FriendlyName)
    Write-Host ("    DeviceDesc (base)    : {0}" -f $r.DeviceDesc)
    Write-Host ("    device instance      : {0}" -f $r.DeviceInstanceID)
    Write-Host ("    resolution           : {0}" -f $r.Resolution)
    Write-Host ("    VERDICT              : {0}" -f $r.Verdict) -ForegroundColor $colour
}

# -- 5. What the app thinks it applied ---------------------------------------
Write-Head 'config.json - adapterNames (what the app believes it applied)'

if (Test-Path $configPath) {
    try {
        $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
        $overrides = @($cfg.adapterNames)
        if ($overrides.Count -gt 0) {
            foreach ($o in $overrides) {
                Write-Host ''
                Write-Host ("    deviceInstanceId    : {0}" -f $o.deviceInstanceId)
                Write-Host ("    currentFriendlyName : {0}" -f $o.currentFriendlyName)
                Write-Host ("    originalFriendlyName: {0}" -f $o.originalFriendlyName)
                Write-Host ("    originalWasAbsent   : {0}" -f $o.originalWasAbsent)
                Write-Host ("    renamedOn           : {0}" -f $o.renamedOn)

                $live = @($report | Where-Object { $_.DeviceInstanceID -eq $o.deviceInstanceId })
                if ($live.Count -eq 1) {
                    if ($live[0].FriendlyName -eq $o.currentFriendlyName) {
                        Write-Host '    -> registry AGREES with config' -ForegroundColor Green
                    } else {
                        Write-Host ("    -> MISMATCH: registry has '{0}', config expected '{1}'" -f $live[0].FriendlyName, $o.currentFriendlyName) -ForegroundColor Red
                    }
                } else {
                    Write-Host '    -> no live NIC matches this device instance' -ForegroundColor DarkGray
                }
            }
        } else {
            Write-Host '    adapterNames is empty - the app has never recorded a rename.' -ForegroundColor Yellow
            Write-Host '    (If you just tried to rename, the flow aborted before the write.)' -ForegroundColor Yellow
        }
    } catch {
        Write-Host ("    Could not parse config.json: {0}" -f $_) -ForegroundColor Red
    }
} else {
    Write-Host ("    config.json not found at {0}" -f $configPath) -ForegroundColor Yellow
}

# -- 6. The app's own account of the last rename attempt ----------------------
Write-Head 'ui.log - recent "Rename flow" lines (the app own account)'

if (Test-Path $uiLogPath) {
    $lines = @(Select-String -Path $uiLogPath -Pattern 'Rename flow' -ErrorAction SilentlyContinue |
               Select-Object -Last 15)
    if ($lines.Count -gt 0) {
        foreach ($l in $lines) { Write-Host ("    {0}" -f $l.Line.Trim()) }
    } else {
        Write-Host '    No "Rename flow" lines - the flow never started (button not wired?).' -ForegroundColor Red
    }
} else {
    Write-Host ("    ui.log not found at {0} (v2.5.2+ only)." -f $uiLogPath) -ForegroundColor Yellow
}

Write-Host ''
Write-Host 'Done. Nothing was modified.' -ForegroundColor Cyan

if ($OutFile) {
    try { Stop-Transcript | Out-Null } catch { }
    Write-Host ''
    Write-Host ("Transcript written to: {0}" -f $OutFile) -ForegroundColor Cyan
}
Write-Host ''
