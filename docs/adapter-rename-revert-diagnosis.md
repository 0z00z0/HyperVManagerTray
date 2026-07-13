# Diagnosis: "Rename network adapter" is a silent no-op reported as success (issue #15 revert)

**Symptom (from the user's screenshots).** The whole rename flow completes and reports success —
rename dialog → confirmation (`From: Realtek USB GbE Family Controller #2  To: Dell docking
(Petterhaugen)`) → "The adapter description was set to …" → user clicks **Yes** to restart → "Adapter
restarted. The new name should now appear everywhere." **But** in Windows (Settings, the adapter
list, Device Manager) the description still reads `Realtek USB GbE Family Controller #2`. The new name
appears **only inside the app's own dialogs**. The value never lands on disk, yet every step claims
success.

**Ruled out (verified).** `config.json` does not re-apply the old name on startup — nothing rewrites
the value; the "app reverts it" theory is dead. The failure is entirely in the **write path** in
`Helpers/AdapterRenamer.cs`.

---

## Ranked root cause

### 1 — PRIMARY: the write bound to the ANSI SetupAPI entry point while being fed a UTF-16 buffer

The original write used SetupAPI:

```csharp
[DllImport("setupapi.dll", SetLastError = true)]                       // ← no CharSet
private static extern bool SetupDiSetDeviceRegistryProperty(
    IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, uint Property,
    byte[] PropertyBuffer, uint PropertyBufferSize);
...
var buffer = Encoding.Unicode.GetBytes(friendlyName + '\0');           // ← UTF-16 bytes
SetupDiSetDeviceRegistryProperty(set, ref devInfo, SPDRP_FRIENDLYNAME, buffer, (uint)buffer.Length);
```

`DllImportAttribute` defaults to **`CharSet.Ansi`** and `ExactSpelling = false`. setupapi.dll exports
only `SetupDiSetDeviceRegistryPropertyA` and `…W` (no bare name), so the marshaler binds to the
**ANSI** variant `SetupDiSetDeviceRegistryPropertyA`. That function interprets `PropertyBuffer` as an
**ANSI** (code-page) string for a `REG_SZ` property. It is handed the **UTF-16** bytes
`44 00 65 00 6C 00 …` ("D","\0","e","\0","l",…). The ANSI→wide conversion / string-length scan stops
at the first `00` byte (index 1), so at best a single-character value ("D") is produced — the intended
name never lands. The call still returns **TRUE**, so the app's `if (!…) throw` guard never fires.

- SPDRP_FRIENDLYNAME (`0x0000000C`) and the byte-length arithmetic (`buffer.Length`, UTF-16 + NUL)
  were both **correct** — the defect is purely the A-vs-W entry-point binding. The neighbouring
  `SetupDiOpenDeviceInfo` P/Invoke *does* specify `CharSet.Unicode`, which is exactly why it worked
  and the write silently didn't: the mismatch is isolated to this one declaration.
- Consistent with the evidence: because the restart (§3) doesn't force NDIS to re-read reliably on a
  USB dock, `InterfaceDescription`/`ifDescr` stays cached at `…#2` even if the raw `FriendlyName` was
  corrupted to "D" — so the user sees "#2" everywhere while the on-disk value is quietly wrong.

**Discriminator (run the diagnostic in §A):** if the live `Enum\…\FriendlyName` now reads **"D"** (or
another 1-char / mojibake value), this root cause is confirmed. If it still reads the full
`Realtek USB GbE Family Controller #2` **unchanged**, then the A-variant no-op'd entirely (same class
of bug, same fix). Either way the fix below resolves it.

### 2 — AMPLIFIER: the write result was never verified against disk

Even with a correct API, trusting the returned `BOOL` is not enough — the whole point of #15 is that
the API reported success while nothing persisted. There was **no read-back**, so any lie (or partial
write) surfaced to the user as "success". This is *why the bug is invisible*: the one check that would
have caught it did not exist.

### 3 — SECONDARY: the restart reported success without confirming anything

`OfferDeviceRestart` printed "Adapter restarted. The new name should now appear everywhere." whenever
`RestartDevice` didn't throw. The SetupAPI disable/enable can return success while
`InterfaceDescription`/`ifDescr` has not actually refreshed (some consumers cache it; a USB dock may
defer), and it never re-checked that the name was still on disk afterwards.

### Lower-ranked / not the cause
- **`SetupDiSetDeviceRegistryProperty(SPDRP_FRIENDLYNAME)` not persisting at all** — plausible in
  theory (phantom device-info element), but the investigation confirmed the value *does* live in
  `Enum\…\FriendlyName` and Administrators have FullControl there, so a **direct** registry write is
  strictly more reliable and sidesteps the question. This is the path the fix takes.
- **Wrong device resolved** — ruled out: the GUID chain (`ResolveDeviceInstanceId`) is deterministic,
  unit-tested, and aborts on 0/>1 matches. Untouched by this fix.
- **Bad characters / injection** — ruled out: `ValidateName` restricts to letters/digits/space and
  `- _ ( ) # .`; the write is parameterized (no shell). Untouched.

---

## The fix (code)

**`Helpers/AdapterRenamer.cs` — `WriteFriendlyName`** now does a **direct, parameterized registry
write** of the `FriendlyName` `REG_SZ` on the resolved Enum key, then **reads it back and throws
unless it matches**:

```csharp
using (var key = Registry.LocalMachine.OpenSubKey(EnumKeyPrefix + deviceInstanceId, writable: true))
{
    if (key is null) throw new InvalidOperationException("…device key not found…");
    key.SetValue("FriendlyName", friendlyName, RegistryValueKind.String);
    key.Flush();
}
var (present, written) = ReadFriendlyName(deviceInstanceId);
if (!AdapterNameRules.FriendlyNameApplied(present, written, friendlyName))
    throw new InvalidOperationException("…did not persist… No change was applied.");
```

- **Eliminates the marshaling bug entirely** — no P/Invoke, no A/W, no manual byte buffer. The old
  `SetupDiSetDeviceRegistryProperty` P/Invoke and the `SPDRP_FRIENDLYNAME` constant were removed
  (dead + dangerous).
- **Makes failure visible** — a missing key, an ACL denial (`SecurityException`/
  `UnauthorizedAccessException` from `OpenSubKey(writable:true)`), or a value that doesn't stick all
  throw, and the caller (`ApplyRenameAsync`/`ResetAdapterNameAsync`) already surfaces the exception as
  an error dialog. **Success is only ever reported after the value is confirmed on disk.**
- **Safety unchanged** — still one value (`FriendlyName`) on the one GUID-resolved Enum key; never
  `DeviceDesc`, never the Class key; no shell string. Consent gating in `TrayMenu` is unchanged.

**`Helpers/AdapterNameRules.cs`** — new pure, unit-tested helper
`FriendlyNameApplied(present, onDisk, intended)` (present && ordinal-exact) used by both the write
read-back and the post-restart check.

**`UI/TrayMenu.cs` — `OfferDeviceRestart`** — after `RestartDevice`, re-reads the FriendlyName and
only shows "Adapter restarted…" when `FriendlyNameApplied` is true; otherwise warns that Windows may
have reset it. "restarted" is no longer claimed blind.

**Confirmability.** Root-cause reasoning and the fix are code-confirmable (build + tests green). That
the value now **persists system-wide** is inherently a live-device behaviour and needs the user's
one-time check on a spare adapter (§B) — but the read-back guarantees the app can no longer *lie*
about it regardless.

---

## §A — READ-ONLY diagnostic (safe to paste; performs NO writes, NO device changes)

Run in an **elevated** PowerShell (same privilege as the app — the `Enum` subtree needs it to read).
It reveals, for the target adapter: the live Enum-key `FriendlyName`, the Class-key
`NetCfgInstanceId`/`DriverDesc`, the NDIS `InterfaceDescription`, and .NET
`NetworkInterface.Description`.

```powershell
& {
  # <-- set this to the adapter's CURRENT description exactly as the app shows it:
  $targetDesc = 'Realtek USB GbE Family Controller #2'

  $nic = Get-NetAdapter | Where-Object { $_.InterfaceDescription -eq $targetDesc } | Select-Object -First 1
  if (-not $nic) { $nic = Get-NetAdapter | Where-Object { $_.InterfaceDescription -like "*$targetDesc*" } | Select-Object -First 1 }
  if (-not $nic) { Write-Host "No adapter matched '$targetDesc' — adjust `$targetDesc." -ForegroundColor Yellow; return }

  $guid = $nic.InterfaceGuid
  '=== NDIS (Get-NetAdapter) ==='
  $nic | Select-Object Name, InterfaceDescription, InterfaceGuid, MacAddress, Status | Format-List

  '=== .NET NetworkInterface.Description (what the app reads) ==='
  [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() |
    Where-Object { $_.Id -eq $guid } | Select-Object Id, Name, Description | Format-List

  $classRoot = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}'
  $devInst = $null
  Get-ChildItem $classRoot -ErrorAction SilentlyContinue | ForEach-Object {
    $p = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
    if ($p.NetCfgInstanceId -eq $guid) {
      "=== Class key ($($_.PSChildName)) ==="
      $p | Select-Object NetCfgInstanceId, DriverDesc, DeviceInstanceID | Format-List
      $devInst = $p.DeviceInstanceID
    }
  }

  if ($devInst) {
    "=== Enum key FriendlyName (GROUND TRUTH on disk) — $devInst ==="
    $e = Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Enum\$devInst" -ErrorAction SilentlyContinue
    [pscustomobject]@{
      FriendlyName = if ($e -and ($e.PSObject.Properties.Name -contains 'FriendlyName')) { '"' + $e.FriendlyName + '"' } else { '<ABSENT>' }
      DeviceDesc   = $e.DeviceDesc
    } | Format-List
  } else {
    Write-Host "Could not resolve DeviceInstanceID from the Class key for $guid." -ForegroundColor Yellow
  }
}
```

**How to read it:** the **Enum-key `FriendlyName`** line is the ground truth. Today (before the fixed
build) expect it to be either `"D"`/mojibake (confirms root cause 1) or the unchanged
`"Realtek USB GbE Family Controller #2"`. After a rename with the fixed build it must equal the new
name; `Get-NetAdapter InterfaceDescription` and `.NET Description` follow after the device restart.

---

## §B — Minimal manual test (on a SPARE adapter only — never the active bridged NIC)

Disable/enable briefly drops the adapter's link, so **do not test on the NIC carrying your bridged
connection.** Use a spare: an unplugged/second USB dock instance, a disabled adapter, or a Hyper-V
internal-only vSwitch adapter.

1. **Baseline:** set `$targetDesc` in §A to the spare's current description and run it. Note the
   Enum-key `FriendlyName` (or `<ABSENT>`).
2. **Rename** the spare in the app (tray → rename → e.g. `Test Rename 1`). Decline the restart for now
   (click **No**).
3. **Prove the write landed:** re-run §A. The Enum-key `FriendlyName` must now read `"Test Rename 1"`.
   *This alone proves the fix* — the value is on disk, independent of any restart.
   - If instead the app showed an **error** ("did not persist"/ACL) — that is the fix working
     correctly by refusing to claim a false success; capture the message.
4. **Propagate:** rename again and this time click **Yes** to restart. The app should report success
   only after its post-restart re-check. Confirm `Get-NetAdapter` / Settings now show the new name.
5. **Restore:** use **Reset** in the app to put the original description back; re-run §A to confirm the
   original `FriendlyName` is restored (Reset restores the saved value — it never deletes it).

Expected with the fix: step 3 always reflects reality — either the new name is on disk **or** the app
told you (with the Win32/registry reason) that it wasn't. The old behaviour — new name only in the
app's dialogs, `#2` everywhere in Windows, yet "success" — can no longer occur.
