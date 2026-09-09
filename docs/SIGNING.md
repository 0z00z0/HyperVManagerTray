# Code signing

Hyper-V Manager Tray is Authenticode-signed — both the application executable and the
installer — with the publisher's certificate **`CN=ZeroZero Software`**.

## What we do (and why it's as strong as a free cert allows)

| Property | Status | Why it matters |
|----------|--------|----------------|
| SHA-256 digest | ✅ | Modern hash; SHA-1 is deprecated. |
| RFC-3161 timestamp | ✅ (`timestamp.digicert.com`) | The signature stays **valid after the certificate expires** — installers don't "rot". |
| App exe **and** installer signed | ✅ | The installer never embeds an unsigned exe (CI signs the exe first, then recompiles + signs the installer). |
| Same cert locally and in CI | ✅ | The cert is delivered to GitHub Actions via the `CODE_SIGN_PFX` / `CODE_SIGN_PASSWORD` secrets, so tag-built releases are signed identically to local builds. |
| Publicly *trusted* chain | ❌ (self-signed) | A paid/managed cert is required for this — see below. |

## The honest limitation

The certificate is **self-signed**. Windows only treats the signature as "trusted" on machines
that have explicitly imported it. On any other machine, the signature is *present and valid* but
the publisher is *untrusted*, so you'll still see **SmartScreen / "Unknown Publisher"** on first
run. Signing with a self-signed cert does **not** remove those prompts for the general public —
nothing free fully does.

What it *does* give you: tamper-evidence (the SHA-256 in each release), a stable publisher
identity, non-expiring signatures (timestamping), and the option for users/admins to trust the
publisher once (below).

## The signature is checked before an update runs

Self-update never runs a downloaded installer unchecked. Two checks, in this order, each answering
a question the other cannot:

1. **SHA-256 against the hash the release publishes** in its release body — whether the download is
   whole. A release publishing no hash, or more than one, is refused and nothing is downloaded.
2. **Authenticode signature and signer** — whether the file is this publisher's. Because the
   certificate is self-signed, a machine that has not imported it does not trust the chain, and
   there the subject alone would accept any self-signed certificate spelling the same name; the
   certificate's SHA-256 thumbprint must match as well.

Both run again at the moment of launch, so the bytes that were checked are the bytes that run.
A refused file is deleted and the user is told nothing has run.

The pin is `scripts\ZeroZeroSoftware.cer` itself, embedded in the application as a resource
(`HyperVManagerTray.csproj`) and read at run time by `Helpers\ExpectedPublisher.cs` — the subject
and the thumbprint both come out of the certificate's bytes. No thumbprint is written into source,
so the application and the release workflow's own verification check against one file.

## Trust the publisher (optional, per machine)

The public certificate (public key only — safe to share) is shipped at
[`scripts/ZeroZeroSoftware.cer`](../scripts/ZeroZeroSoftware.cer) and attached to each release.
To make Windows trust binaries signed by this publisher on your machine:

```powershell
# Per-user (no admin): trust as a root + publisher for the current user
Import-Certificate -FilePath .\ZeroZeroSoftware.cer -CertStoreLocation Cert:\CurrentUser\Root
Import-Certificate -FilePath .\ZeroZeroSoftware.cer -CertStoreLocation Cert:\CurrentUser\TrustedPublisher
```

Verify a downloaded file matches the published thumbprint before trusting it:

```powershell
(Get-AuthenticodeSignature .\HyperVManagerTray-Setup.exe).SignerCertificate.Thumbprint
# Expected: 4909D644147756958E31783CF9D5926873522197
```

> Only import a code-signing certificate you trust. Importing it as a root means your machine
> will trust **anything** signed by that publisher.

## Upgrade paths (to remove the prompts for everyone)

When public, prompt-free distribution becomes worth it:

1. **SignPath Foundation** — free Authenticode certificates for qualifying open-source projects.
   The best $0 option for a publicly-trusted signature.
2. **Azure Trusted Signing** — ~$10/month, Microsoft-managed cert, integrates cleanly with CI.
3. A traditional **OV/EV code-signing certificate** from a CA (DigiCert, Sectigo, …).

Any of these drops in by replacing the `CODE_SIGN_PFX` secret (or swapping the signtool call for
the provider's action); the rest of the pipeline is unchanged.

## For maintainers — rotating the CI signing secret

```powershell
# Export the local cert (with key) to a temp PFX, base64 it, push as repo secrets.
$cert = Get-ChildItem Cert:\CurrentUser\My |
  Where-Object { $_.Subject -eq 'CN=ZeroZero Software' -and
                 $_.EnhancedKeyUsageList.ObjectId -contains '1.3.6.1.5.5.7.3.3' } |
  Sort-Object NotAfter -Descending | Select-Object -First 1
$pw  = -join ((48..57)+(65..90)+(97..122) | Get-Random -Count 48 | ForEach-Object {[char]$_})
$pfx = Join-Path $env:TEMP 'zzs-codesign.pfx'
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password (ConvertTo-SecureString $pw -AsPlainText -Force) | Out-Null
[Convert]::ToBase64String([IO.File]::ReadAllBytes($pfx)) | gh secret set CODE_SIGN_PFX --repo 0z00z0/HyperVManagerTray
gh secret set CODE_SIGN_PASSWORD --body $pw --repo 0z00z0/HyperVManagerTray
Remove-Item $pfx -Force
```

The one-time local setup that creates + trusts the cert is `scripts\sign.ps1 -Setup`.

## For maintainers — rotating the certificate itself

Installed copies pin the certificates the build they came from embedded, so a certificate that
first appears in the release that starts signing with it is refused by every copy already out
there. The order is fixed:

1. Add the new `.cer` beside `scripts\ZeroZeroSoftware.cer` and add an `EmbeddedResource` line for
   it in `HyperVManagerTray.csproj` and `Tests\HyperVManagerTray.Tests.csproj`. Every embedded
   `.cer` is a pin, and all of them must carry the same subject.
2. Release. Installed copies now accept both certificates.
3. Only then swap `CODE_SIGN_PFX` to the new certificate and update the thumbprint quoted under
   *Trust the publisher* above.
4. Remove the old `.cer` a release later, once nothing in the field still needs it.
