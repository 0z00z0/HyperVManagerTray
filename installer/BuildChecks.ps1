# Checks build-installer.ps1 makes before it builds anything. Dot-sourced by it, and kept in their own
# file so each check can be run on its own without publishing or compiling.
# ASCII only: Windows PowerShell reads a file without a byte-order mark as the ANSI code page.

function Get-UncommittedChanges {
    <#
    .SYNOPSIS
        The working tree's uncommitted changes, as 'git status --porcelain' lines, leaving out a change
        to the project file that touches nothing but its <Version> line - the bump build-installer.ps1
        writes itself.
    #>
    param(
        [Parameter(Mandatory)] [string] $Root,
        [Parameter(Mandatory)] [string] $ProjectFile   # relative to $Root, with forward slashes
    )

    Push-Location $Root
    try {
        $status = @(& git status --porcelain --untracked-files=all 2>$null)
        if ($LASTEXITCODE -ne 0) {
            throw "Cannot tell whether '$Root' is committed: 'git status' failed. An installer is built only from a git checkout."
        }

        $changes = @()
        foreach ($line in $status) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            $path = $line.Substring(3).Trim('"')
            if ($path -eq $ProjectFile -and $line.Substring(0, 2) -match '^(M |MM| M)$') {
                # Every added or removed line must be the version element, or the change is someone's edit.
                $diff = @(& git diff -U0 HEAD -- $ProjectFile 2>$null) |
                        Where-Object { $_ -match '^[+-]' -and $_ -notmatch '^(\+\+\+|---) ' }
                $other = @($diff | Where-Object { $_ -notmatch '^[+-]\s*<Version>\d+\.\d+\.\d+</Version>\s*$' })
                if ($diff.Count -gt 0 -and $other.Count -eq 0) { continue }
            }
            $changes += $line
        }
        return ,$changes
    }
    finally {
        Pop-Location
    }
}

function Assert-CommittedTree {
    <#
    .SYNOPSIS
        Refuses to go on while the working tree differs from HEAD, apart from the version bump this
        script makes. The installer is published from the working tree, so a build made before its
        changes are committed ships code that no commit contains, and nothing in it could say which.
    #>
    param(
        [Parameter(Mandatory)] [string] $Root,
        [Parameter(Mandatory)] [string] $ProjectFile
    )

    $changes = Get-UncommittedChanges -Root $Root -ProjectFile $ProjectFile
    if ($changes.Count -gt 0) {
        $shown = ($changes | Select-Object -First 20) -join "`n    "
        $more  = if ($changes.Count -gt 20) { "`n    ... and $($changes.Count - 20) more" } else { "" }
        throw ("Refusing to build an installer: the working tree has $($changes.Count) uncommitted change(s), " +
               "so the installer would contain code that no commit holds. Commit or stash them first:`n    " +
               $shown + $more)
    }
}

function Get-InnoSetupVersion {
    <#
    .SYNOPSIS
        The version of the Inno Setup compiler at $Iscc: Major, and Display for messages.
    .DESCRIPTION
        ISCC.exe carries no file version, so the major version is read from the banner it prints when
        started with no script - which compiles nothing - and the full version, where there is one, from
        the uninstaller its installer left beside it. A banner that cannot be read gives major 0.
    #>
    param([Parameter(Mandatory)] [string] $Iscc)

    # A Process rather than '&': Windows PowerShell turns a native command's stderr into an error record,
    # which the build script's ErrorActionPreference of Stop would make fatal.
    $psi = New-Object System.Diagnostics.ProcessStartInfo $Iscc
    $psi.UseShellExecute        = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $psi.CreateNoWindow         = $true
    $process = [System.Diagnostics.Process]::Start($psi)
    $stderr  = $process.StandardError.ReadToEndAsync()
    $banner  = $process.StandardOutput.ReadToEnd() + $stderr.Result
    $process.WaitForExit()

    $major = 0
    $m = [regex]::Match($banner, 'Inno Setup (\d+)(?:\.\d+)*\s+Command-Line Compiler')
    if ($m.Success) { $major = [int]$m.Groups[1].Value }

    $display = if ($major -gt 0) { "$major" } else { "of unknown version" }
    $uninstaller = Join-Path (Split-Path $Iscc -Parent) "unins000.exe"
    if ($major -gt 0 -and (Test-Path $uninstaller)) {
        $full = "$((Get-Item $uninstaller).VersionInfo.ProductVersion)".Trim()
        if ($full -match '^(\d+)\.\d+\.\d+$' -and [int]$Matches[1] -eq $major) { $display = $full }
    }

    [pscustomobject]@{ Major = $major; Display = $display }
}

function Find-InnoSetupCompiler {
    <#
    .SYNOPSIS
        The path of an Inno Setup compiler of at least $RequiredMajor, preferring an Inno Setup 7
        installation over whatever is first on the command path. Throws, naming what was found and what
        is required, when only an older one exists: the release workflow compiles with a pinned 7.x, and
        an installer compiled here with 6.x would differ from the one released.
    #>
    param(
        [int] $RequiredMajor = 7,
        [string[]] $Candidates = @(
            "${env:ProgramFiles}\Inno Setup 7\ISCC.exe",
            "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
            "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe",
            (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source,
            "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "${env:ProgramFiles}\Inno Setup 6\ISCC.exe")
    )

    $seen  = @{}
    $older = @()
    foreach ($path in $Candidates) {
        if ([string]::IsNullOrWhiteSpace($path) -or -not (Test-Path $path)) { continue }
        $key = (Resolve-Path $path).Path.ToLowerInvariant()
        if ($seen.ContainsKey($key)) { continue }
        $seen[$key] = $true

        $version = Get-InnoSetupVersion -Iscc $path
        if ($version.Major -ge $RequiredMajor) {
            return [pscustomobject]@{ Path = $path; Version = $version.Display }
        }
        $older += "Inno Setup $($version.Display) at $path"
    }

    if ($older.Count -eq 0) {
        throw "Inno Setup (ISCC.exe) not found. Inno Setup $RequiredMajor or later is required; install it once with:  winget install JRSoftware.InnoSetup"
    }
    throw ("Inno Setup $RequiredMajor or later is required, as the release build uses it, but only an older " +
           "version was found: $($older -join '; '). Install Inno Setup $RequiredMajor with:  winget install JRSoftware.InnoSetup")
}
