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
