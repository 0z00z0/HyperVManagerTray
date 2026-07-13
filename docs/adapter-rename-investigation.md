# Investigation: renaming the network adapter's displayed description (Option B)

**Scope:** Option B from the issue body — changing the adapter *description* string itself, system-wide, so the tray app, Hyper-V Manager MMC, Windows Settings and Device Manager all show the friendly name.
**Method:** read-only live inspection of this exact host (which has **two** Realtek dock instances — the real multi-dock scenario), corroborated with Microsoft documentation and community-established practice. **No writes were performed.**
**Date:** 2026-07-13. Host: Windows 11 Pro 26100, the machine from the issue screenshots.

---

## 1. Headline finding — the issue targets the wrong registry value

Option B as originally written says "rewrite the driver's `DriverDesc`". Live inspection shows **the `#2` string does not live in `DriverDesc` at all**:

| Store | Value on this host (connected dock) | Notes |
|---|---|---|
| Class key `DriverDesc` (`…\Control\Class\{4d36e972-…}\0022`) | `Realtek USB GbE Family Controller` — **no #2** | INF-written base name; identical for *both* dock instances |
| Enum key `FriendlyName` (`…\Enum\USB\VID_0BDA&PID_8153\000002000000`) | `Realtek USB GbE Family Controller #2` | **This is where the displayed string lives** |
| Enum key `DeviceDesc` | `@oem241.inf,%rtl8153.devicedesc%;Realtek USB GbE Family Controller` | INF-indirect localized string — must never be touched |
| NDIS `ifDescr` (= `Get-NetAdapter InterfaceDescription`, .NET `NetworkInterface.Description`) | `Realtek USB GbE Family Controller #2` | Derived from `FriendlyName` when present; **this is what HyperVManagerTray, Hyper-V MMC and Windows Settings display** |
| WMI `Win32_NetworkAdapter.Description` | `Realtek USB GbE Family Controller` — no #2 | Reads the base description, *not* FriendlyName — one consumer that will **not** reflect a rename |

When Windows installs a second identical device, PnP writes `FriendlyName = "<DeviceDesc> #N"` on the new instance; NDIS then builds the interface description (ifDescr) from `FriendlyName` when it exists, else from `DeviceDesc`. So **the correct implementation of Option B is: write the `FriendlyName` value** (equivalently, the documented device property `DEVPKEY_Device_FriendlyName` via SetupAPI `SetupDiSetDeviceRegistryProperty(SPDRP_FRIENDLYNAME)` / CM property APIs).

This matches the community-established method (Microsoft Q&A and multiple tutorials confirm `Rename-NetAdapter` can only change the *alias*, and that changing the *description* is done by editing `FriendlyName` under `HKLM\SYSTEM\CurrentControlSet\Enum\…`).

## 2. Live evidence from this host

Both dock instances found (read-only):

```
Class {4d36e972-e325-11ce-bfc1-08002be10318}:
  Subkey 0014: DriverDesc="Realtek USB GbE Family Controller"
               NetCfgInstanceId={FB095B22-2E76-4DEC-BA3F-0084EA984B38}
               DeviceInstanceID=USB\VID_0BDA&PID_8153\001000001      ← dock A (not present now)
  Subkey 0022: DriverDesc="Realtek USB GbE Family Controller"
               NetCfgInstanceId={BECDE8F3-29F7-41E4-9862-8097B2BB14EF}
               DeviceInstanceID=USB\VID_0BDA&PID_8153\000002000000   ← dock B ("Petterhagen", connected, the "#2")

PnP FriendlyName: dock A = "Realtek USB GbE Family Controller"
                  dock B = "Realtek USB GbE Family Controller #2"
```

Two load-bearing verifications:

- **Reliable device selection exists.** The Class subkey carries both `NetCfgInstanceId` (= the adapter GUID the app already has from `NetworkInterface.Id`/`Get-NetAdapter InterfaceGuid`) *and* `DeviceInstanceID`. That gives a deterministic chain **InterfaceGuid → Class subkey → DeviceInstanceID → Enum key** with zero name-based matching — eliminating the "renamed the wrong dock" risk the issue worried about.
- **ACL: `BUILTIN\Administrators` has FullControl on the Enum device key** (verified live). The app already runs `requireAdministrator`, so it can write `FriendlyName` without SYSTEM impersonation or ownership changes. (The safer, documented write path is still SetupAPI/CM — see §5.)

## 3. Answers to the issue's three risk questions

**Would something likely break permanently? → No, with a correctly scoped write; nothing observed suggests bricking.**
`FriendlyName` is a display string. Hyper-V external vSwitch bindings, the app's own rule matching (MAC-based), and TCP/IP configuration all key off the adapter GUID/MAC, not the description. Worst realistic outcomes are (a) confusing/duplicate names — cosmetic, recoverable; (b) a rename being silently reverted by a later driver update/reinstall (PnP regenerates FriendlyName during device install — the feature must expect renames to be *non-durable* across driver updates and offer re-apply); (c) an implementation bug writing the wrong value or wrong key — which is a code-quality risk, not an inherent one, addressed in §5. Recovery paths always exist: restore the saved original value, or reinstall the driver (regenerates defaults).

**Would a computer restart be needed? → Probably not a full restart — but a device (NIC) restart, which drops that adapter's link briefly.**
NDIS reads `FriendlyName` when the adapter registers. Expectation from mechanism + community reports: writing FriendlyName updates Device Manager immediately, but `InterfaceDescription`/ifDescr (what the app and Hyper-V MMC show) refreshes only after a **device disable/enable cycle** (`Disable-PnpDevice`/`Enable-PnpDevice` or `pnputil /restart-device`), with a full reboot as the guaranteed fallback. **This must be confirmed in the prototype phase** (it's the main open question — some consumers cache descriptions in the NSI persistent store). UX consequence either way: renaming the adapter that carries the active bridged connection will briefly interrupt networking on it — precisely the sensitive path this app manages — so the warning dialog must say "your network connection on this adapter will drop for a few seconds" (or "…until restart"), not merely "a restart may be needed".

**Allowed characters — can a bad name crash the system? → No crash vector via the registry itself; the real danger is command injection in a careless implementation.**
`REG_SZ` accepts any Unicode without embedded NUL; no documented character set corrupts Windows by being stored in FriendlyName. The genuine risk: this app is **elevated**, so if the rename were implemented by string-concatenating into `reg.exe`/PowerShell, a crafted name becomes elevated arbitrary code execution. Mitigation is absolute: write via .NET `Microsoft.Win32.Registry` or SetupAPI P/Invoke only — never a shell string. A conservative validation policy is still sensible for sanity/UI reasons (§5).

## 4. What updates and what doesn't (propagation map)

| Consumer | Reflects FriendlyName rename? |
|---|---|
| HyperVManagerTray dashboard/tray (via .NET `NetworkInterface.Description`) | ✅ after device restart (to be confirmed) |
| `Get-NetAdapter InterfaceDescription`, Windows Settings, Hyper-V Manager MMC vSwitch UI | ✅ same |
| Device Manager device list | ✅ (immediately or after refresh) |
| WMI `Win32_NetworkAdapter.Description` | ❌ stays the base description (verified live: it already shows no "#2" today) |
| Connection name / `InterfaceAlias` ("Petterhagen - Dell docking") | Unaffected — separate, already user-renamable field |

## 5. Risk-minimization requirements for any future implementation

1. **Write exactly one value:** the `FriendlyName` `REG_SZ` on the resolved Enum key — never `DeviceDesc` (INF-indirect), never anything under the Class key (real driver parameters live there).
2. **Resolve the device by GUID chain, never by name:** InterfaceGuid → Class subkey where `NetCfgInstanceId` matches → its `DeviceInstanceID` → Enum key. Abort if the chain resolves to zero or >1 device.
3. **No shell involvement:** .NET Registry API or SetupAPI (`SPDRP_FRIENDLYNAME`) via P/Invoke. The parameterized API boundary is the injection defense; prefer SetupAPI since it's the documented device-property surface and notifies PnP.
4. **Save-before-first-rename + Reset:** persist `{DeviceInstanceID, original FriendlyName (or "absent"), MAC, date}` in `config.json` before the first write. **Reset restores the saved value — never deletes the value** (deleting on a multi-instance device would make ifDescr fall back to the un-numbered `DeviceDesc`, producing two identical descriptions, the exact confusion this feature exists to fix).
5. **Uniqueness check** against all net-class devices' current FriendlyName/DeviceDesc/ifDescr (Windows does not enforce this — two devices *can* end up with identical descriptions).
6. **Validation policy:** trim; 1–200 chars; printable only (no control chars); disallow leading/trailing whitespace; conservatively restrict to letters/digits/space and `- _ ( ) # .` — not because Windows requires it, but to keep every downstream consumer (logs, JSON config, WMI queries, UI) trivially safe.
7. **Explicit consent flow:** warning dialog → rename → offer device restart now (with link-drop warning) or later; detect and surface a driver-update revert (compare stored vs live on startup).
8. **Prototype on a VM first** (§6) before the feature touches a real host.

## 6. Open questions for the prototype phase (must be answered before implementation)

- [ ] Does ifDescr refresh after `disable/enable` alone, or only after reboot? (Test on a throwaway VM with two identical NICs.)
- [ ] Does Hyper-V's vSwitch manager UI pick up the new description without host service restart?
- [ ] Does a driver re-install/update actually revert FriendlyName on this Realtek driver specifically?
- [ ] Does writing via SetupAPI (vs raw registry) trigger the PnP re-read without a device restart?
- [ ] Behavior when the device is renamed while *not present* (dock unplugged) — allowed and applied on next arrival?

## 7. Recommendation

Option B is **feasible and lower-risk than the issue initially assumed** — because the correct target (`FriendlyName`) is a per-device display value with a documented API, an admin-writable ACL (verified), a deterministic GUID-based selection chain (verified), and no coupling to driver behavior or Hyper-V bindings. The two real costs are: a brief link drop (or reboot) to propagate, and non-durability across driver updates. Neither is prohibitive; both must be surfaced honestly in the UX.

**Keep as `idea` until the §6 prototype checklist is executed on a VM.** Suggested next step when picked up: a 30-minute manual prototype on a disposable VM (add two identical NICs, rename one via `Set-ItemProperty` on FriendlyName, test disable/enable vs reboot propagation), then implement per §5.
