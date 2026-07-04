<#
.SYNOPSIS
    Code-signs HyperVManagerTray.exe with a self-signed certificate.

.DESCRIPTION
    Run once with -Setup to create a self-signed code-signing certificate in the
    current user's store and register it as a trusted root + trusted publisher,
    so Windows treats the signature as valid (no "Unknown Publisher" UAC banner).

    Without -Setup, the script signs the target executable using the existing
    certificate. This mode is invoked automatically by the Release build (see the
    SignOutput target in HyperVManagerTray.csproj) and exits 0 if no certificate
    is found, so it never breaks a build.

    To use a real CA-issued certificate instead, import it into Cert:\CurrentUser\My
    with the same -Subject and skip -Setup; signing picks it up by subject name.

    NOTE: This project signs with the self-signed certificate CN=ZeroZero Software.
    Run -Setup once to create + trust it in the current user's store.

.EXAMPLE
    .\scripts\sign.ps1 -Setup          # one-time: create + trust the certificate
    .\scripts\sign.ps1                 # sign the latest Release build
#>
[CmdletBinding()]
param(
    [switch] $Setup,
    [string] $Path,                                        # exe to sign (defaults to Release output)
    [string] $Subject      = "CN=ZeroZero Software",
    [string] $TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"

# Returns the newest non-expired code-signing cert matching $Subject, or $null.
# Filters by the Code Signing EKU (OID 1.3.6.1.5.5.7.3.3) rather than the
# -CodeSigningCert dynamic parameter, which is unreliable under Windows PowerShell 5.1.
function Get-SigningCertificate {
    Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Subject -eq $Subject -and
            $_.NotAfter -gt (Get-Date) -and
            $_.EnhancedKeyUsageList.ObjectId -contains '1.3.6.1.5.5.7.3.3'
        } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
}

# Trusts an existing cert for the current user (idempotent). Importing into
# CurrentUser\Root pops a one-time Windows consent dialog  -  that's expected and
# must be accepted interactively; it cannot be auto-confirmed.
function Set-CertTrust {
    param([Parameter(Mandatory)] $Cert)
    $pub = Join-Path $env:TEMP "zerozerosoftware-pub.cer"
    try {
        Export-Certificate -Cert $Cert -FilePath $pub | Out-Null
        Import-Certificate -FilePath $pub -CertStoreLocation Cert:\CurrentUser\Root             | Out-Null
        Import-Certificate -FilePath $pub -CertStoreLocation Cert:\CurrentUser\TrustedPublisher | Out-Null
        Write-Host "Certificate trusted for the current user (thumbprint $($Cert.Thumbprint))."
    }
    finally {
        Remove-Item $pub -ErrorAction SilentlyContinue
    }
}

# Creates the self-signed cert and trusts it for the current user.
function New-TrustedSigningCertificate {
    Write-Host "Creating self-signed code-signing certificate '$Subject'..."
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $Subject `
        -CertStoreLocation Cert:\CurrentUser\My `
        -KeyUsage DigitalSignature `
        -KeyExportPolicy Exportable `
        -NotAfter (Get-Date).AddYears(5) `
        -FriendlyName "ZeroZero Software Code Signing"

    Set-CertTrust -Cert $cert
    return $cert
}

# -- Setup mode --------------------------------------------------------------
if ($Setup) {
    $existing = Get-SigningCertificate
    if ($existing) {
        # Cert exists (e.g. created on a previous run)  -  ensure it is trusted.
        # Re-importing is idempotent; accept the consent dialog if it appears.
        Write-Host "A signing certificate for '$Subject' already exists; ensuring it is trusted..."
        Set-CertTrust -Cert $existing
    }
    else {
        New-TrustedSigningCertificate | Out-Null
    }
    return
}

# -- Sign mode ---------------------------------------------------------------

# Default to the Release apphost when no path is supplied.
# This script lives in scripts\, so the project root is one level up.
if (-not $Path) {
    $repoRoot = Split-Path $PSScriptRoot -Parent
    $Path = Join-Path $repoRoot "bin\Release\net10.0-windows10.0.26100.0\win-x64\HyperVManagerTray.exe"
}

if (-not (Test-Path $Path)) {
    Write-Warning "Nothing to sign: '$Path' does not exist."
    return
}

$cert = Get-SigningCertificate
if (-not $cert) {
    # Don't fail the build  -  just inform the developer how to enable signing.
    Write-Warning "No signing certificate for '$Subject'. Run '.\scripts\sign.ps1 -Setup' first. Skipping."
    return
}

Write-Host "Signing $Path ..."
$result = Set-AuthenticodeSignature `
    -FilePath $Path `
    -Certificate $cert `
    -HashAlgorithm SHA256 `
    -TimestampServer $TimestampUrl

if ($result.Status -eq "Valid") {
    Write-Host "Signed successfully (status: Valid)."
}
elseif ($result.SignerCertificate) {
    # A signature WAS applied, but the chain isn't trusted on this machine  -  typical
    # for the self-signed cert when 'sign.ps1 -Setup' hasn't been accepted here. The
    # file is genuinely signed (and CI signs via signtool regardless), so don't break
    # the build; just tell the developer how to make it verify as Valid locally.
    Write-Warning "Signed '$Path', but the certificate is not trusted on this machine (status: $($result.Status))."
    Write-Warning "Run '.\scripts\sign.ps1 -Setup' and accept the prompt to trust '$Subject' locally."
}
else {
    throw "Signing failed: $($result.Status) - $($result.StatusMessage)"
}
