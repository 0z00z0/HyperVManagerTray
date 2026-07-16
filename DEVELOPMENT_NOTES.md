# Development Notes & Lessons Learned

> A memo of the non-obvious findings from building this app, and the reasons behind the
> design choices — so the same mistakes aren't re-made. Read this before changing how the
> app talks to Hyper-V or the host network.

_Last updated: 2026-06-29 (VM status/metrics/power/guest-IPs migrated from PowerShell polling to native WMI — see the dedicated section below). Previously updated 2026-06-05 (project renamed HyperVNetworkSwitcher → HyperVManagerTray; WinUI 3 migration, Inno Setup installer, `Services/` layout, unit tests, Multiplexor adapter fix, icon redesign, dashboard unification)._

---

## TL;DR — invariants to preserve

1. **Filter out Hyper-V virtual NICs and WFP/NDIS filter adapters** before matching rules or
   picking the "primary" adapter. They share MAC/IP with the real NIC but lie about identity.
2. **Rebind the switch with the two-step `AllowManagementOS $false` → `$true`**, never a single
   `Set-VMSwitch -NetAdapterName … -AllowManagementOS $true`. The single form orphans host vNICs.
3. **Every Hyper-V write must be idempotent** — check current state first, skip if it already
   matches. Otherwise the host network flickers on every launch.
4. **Evaluation is single-flight** (one at a time, coalesced). Don't let switch changes run
   concurrently.
5. **Auto-start is a Scheduled Task, not a `Run` key.** An elevated app can't start from `Run`.
6. **Switch binding + host-vNIC repair still talk to Hyper-V via `powershell.exe -EncodedCommand`,**
   not the PowerShell SDK (`HyperVManager`). **VM status/metrics/power/guest IPs use native WMI**
   instead (`VmService`, `root\virtualization\v2`) — see the dedicated section below before
   touching either. **(Prototype exception: branch `wip/wmi-switch-binding` / issue #17 rewrites
   `HyperVManager` to native WMI too — UNVALIDATED, never run live; see the Phase 2 section below.)**

---

## Hyper-V + Windows networking gotchas

### 1. `AllowManagementOS=$true` moves the physical NIC's IP onto a virtual NIC
When an External switch shares the adapter with the host, Windows creates a
`vEthernet (<switch>)` "Hyper-V Virtual Ethernet Adapter" and the **physical NIC loses its
IPv4 address** to it.

Consequences that bit us:
- `GetBestInterface` (iphlpapi) returns the **virtual** adapter's index, so naive "primary
  adapter" detection shows `Hyper-V Virtual Ethernet Adapter #N` instead of the real NIC.
- Rule CIDR matching fails against the physical NIC (it has no IP) — the IP is on the vNIC.

**Fix:** `AdapterMatcher.SplitAdapters()` separates physical vs Hyper-V-virtual adapters. When
the physical NIC has no IPv4, fall back to the virtual NIC's IP/gateway/DNS for matching and
display, while still using the **physical NIC's alias** for `Set-VMSwitch`. The bridged physical
NIC is identified as: Up + valid 6-byte MAC + no IPv4 (when `GetBestInterface` returned a vNIC).

### 2. WFP/NDIS filter-layer adapters are decoys
Windows exposes extra `NetworkInterface` objects like
`Ethernet-WFP Native MAC Layer LightWeight Filter-0000`. They share the real NIC's MAC and IP
but are **not valid `Set-VMSwitch -NetAdapterName` targets** — binding to one hangs (see #6).

**Fix:** `IsFilterLayerAdapter()` excludes anything whose name/description contains `-WFP `,
`-NDIS `, or `LightWeight Filter`. `StripFilterSuffix()` also strips those suffixes for display
(via `AdapterMatcher.DisplayNameResolver`).

### 2a. The name the app *displays* is not `NetworkInterface.Description`  ⚠️ issue #32

The "rename adapter" feature writes the PnP device's **`FriendlyName`** (`HKLM\SYSTEM\
CurrentControlSet\Enum\<deviceInstanceId>`), which Windows surfaces as the NDIS
`InterfaceDescription`. It does **not** affect `System.Net.NetworkInformation.NetworkInterface.
Description` — a different property, from the IP Helper API, derived from the driver's `DeviceDesc`
plus a `#N` dedupe suffix. The app renamed one string and displayed the other, so a rename was
invisible in the app's own UI even though Windows showed it everywhere.

**Fix:** `AdapterMatcher.DisplayNameResolver` resolves the device (Class-key `NetCfgInstanceId` →
`DeviceInstanceID`) and reads its `FriendlyName`, falling back to `Description` when absent /
ambiguous / unreadable. Two rules when touching this:

- **Display only.** Rule matching (MAC + CIDR) and the picker's software-adapter gate
  (`IsPickerPhysicalAdapter`) must keep reading the raw, un-renamable `NetworkInterface.Description`
  — the display name is user-controllable, so gating on it would let a dock renamed "VPN dock" vanish
  from its own list. `PhysicalAdapterInfo` therefore carries both `Description` and `DisplayName`.
- **Cost.** Device resolution walks the whole net Class key — build one `DisplayNameResolver` per
  enumeration, never one per adapter, and keep it off the UI thread.

### 2b. Windows Network Bridge (Multiplexor Driver) is another decoy  ⚠️ confirmed bug
The Windows "Bridge Connections" feature (ncpa.cpl → select adapters → Bridge) creates a
**Microsoft Network Adapter Multiplexor Driver** adapter (`ms_bridge` / `Network Bridge`).
Unlike WFP adapters, it can appear on the LAN with a real DHCP-assigned IP and a metric-0
default route — so `MatchingNic` found it, selected it as the physical adapter for the rule,
and passed `"Network Bridge"` to `UpdateSwitchBindingAsync`. The result:
- `Set-VMSwitch -NetAdapterName 'Network Bridge' -AllowManagementOS $true` ran
- The MAC Bridge Windows service was set to **auto start** and the Multiplexor Driver bound
  to the Hyper-V switch as its external NIC (confirmed in System event log at 16:28:22–27)
- A second default-route adapter (`Network Bridge`) competed with `vEthernet (Bridged)` —
  `Find-NetRoute` showed outbound traffic going through the wrong one, so the host appeared
  to "use WiFi" even with the cable plugged in
- The subsequent evaluation saw chaos, matched Fallback, and switched the VM to Default Switch

**Root cause:** the Multiplexor Driver passes both `IsHyperVVirtual()` (description doesn't
start with "Hyper-V Virtual") and the old `IsFilterLayerAdapter()` (no WFP/NDIS markers).

**Fix (2026-06-05):** `IsFilterLayerAdapter()` now also excludes any adapter whose name or
description contains `"Multiplexor"` (covers the Microsoft Network Adapter Multiplexor Driver
and any LBFO team adapters, which have the same issue).

**If you see a `Network Bridge` adapter in ncpa.cpl that you didn't intentionally create:**
delete it (right-click → Delete). It will not affect Hyper-V's own `AllowManagementOS` bridge
(`vEthernet (<switch>)`), which is a separate mechanism. To diagnose: run
`Find-NetRoute -RemoteIPAddress 8.8.8.8` — the `InterfaceAlias` should be
`vEthernet (<your switch>)`, not `Network Bridge`.

### 3. Orphaned `vEthernet (<switch>)` NICs accumulate on rebind  ⚠️ the big one
Running `Set-VMSwitch -Name X -NetAdapterName <new> -AllowManagementOS $true` to **rebind a
shared switch to a different adapter** leaves the previous host management vNIC behind. Over
time these pile up (we found **5**). Symptoms:
- Hyper-V Manager **locks the switch settings**: _"Some of the settings cannot be changed
  because you have multiple network adapters for the management operating system."_
- The dead vNICs keep **stale metric-0 default routes**, which **intermittently black-hole
  host traffic** (the host "randomly" can't reach the network).

**Fix (`UpdateSwitchBindingAsync`):** two-step rebind — `Set-VMSwitch -AllowManagementOS $false`
(Windows removes the old vNIC) **then** `Set-VMSwitch -NetAdapterName <new> -AllowManagementOS
$true`. Exactly one management vNIC survives.

**Cleanup recipe** if ghosts already exist (elevated): set `-AllowManagementOS $false`, remove
every leftover `vEthernet (<switch>)*` via `pnputil /remove-device <PnPDeviceID>`, then set
`-AllowManagementOS $true` to recreate a single clean one.

### 4. The two-step rebind is slow (~25 s) and non-atomic
The toggle takes ~22–28 s. Two follow-on hazards:
- **It raced the 30 s PowerShell timeout.** On overrun the process was killed *between*
  `$false` and `$true`, leaving the host adapter with **no vNIC and no IP** until the next
  successful bind. → The bind now uses a **120 s** timeout (`BindTimeout`).
- **A kill/crash mid-sequence strands the host adapter.** Don't `Stop-Process` the app during a
  rebind. Recovery: re-enable "Allow management operating system to share this adapter" on the
  switch (Hyper-V Manager, or `Set-VMSwitch -AllowManagementOS $true`).

### 5. The rebind causes self-induced `NetworkChange` churn (the "double-flip")
Dropping the host vNIC during the toggle fires `NetworkChange` events. Mid-rebind the IP is
gone, so evaluation matches **Fallback** and flips the VM Bridged→Default→Bridged. Overlapping
`async void` timer callbacks made it worse by running concurrently.

**Fix (`NetworkMonitor`):** single-flight evaluation with coalescing — a `SemaphoreSlim(1,1)`
plus an `_evaluatePending` flag. Only one evaluate/apply runs at a time; events that arrive
during it collapse into exactly one follow-up pass that settles correctly.

> Note: reverting to a single-command rebind would remove the double-flip **but** brings back
> #3 (ghost NICs). The two-step + coalescing is the accepted trade-off.

### 6. A bad `Set-VMSwitch` target hangs and wedges everything
Binding to a WFP filter adapter (#2) made `powershell.exe` hang on a WMI/DCOM lookup that never
timed out. Because Hyper-V calls are serialized behind one semaphore, the hang blocked
**all** later calls — the VM never switched and the tray icon never updated.

**Fix:** filter WFP adapters (#2) **and** `ProcessRunner` kills the process tree after a timeout.

### 7. Network flicker on every app launch (idempotency)
The in-memory "skip if unchanged" guards (`_lastApplied`, `_lastBoundAdapterInterface`) start
**empty** on launch, so the first evaluation always re-applied — running the disruptive toggle
and `Connect-VMNetworkAdapter` even when nothing had changed.

**Fix:** both Hyper-V writes check current state first and **skip** when it already matches
(switch already External+sharing+bound; VM NIC already on the target switch). If the check
can't confirm a match it falls through to applying (fail-safe).

---

## .NET / packaging gotchas

### 8. `Microsoft.PowerShell.SDK` breaks self-contained single-file
The in-process runspace calls `PSSnapInReader`, which reads a registry key that is **absent in
self-contained deployments**, returning null and failing to initialise.

**Fix:** spawn `powershell.exe` (Windows PowerShell 5.1, always present) with a Base64
`-EncodedCommand` (sidesteps all quoting). See `HyperVManager` + `ProcessRunner`.

### 9. `HKCU\…\Run` cannot auto-start an elevated app
The app is `requireAdministrator`. Windows launches `Run`-key items with a **standard token**
and silently skips apps that demand elevation — so the old "Run on startup" never actually
started it (and didn't even show in Task Manager until reopened).

**Fix (`StartupManager`):** a Scheduled Task with `/SC ONLOGON /RL HIGHEST`. It runs in the
user's interactive session (tray icon appears) with **no UAC prompt**. The obsolete `Run` value
is cleaned up on toggle (`StartupManager`).

### 10. Pre-`Application.Run()` there is no WinForms `SynchronizationContext`
`SynchronizationContext.Current` is null before the message loop starts, so posting UI updates
via a captured context goes to the thread pool and races the first paint (empty popup on first
click). **Fix (historical, WinForms):** the old `TrayApplication` pre-populated the popup
synchronously. Under WinUI this is gone — the dashboard refreshes itself before `AppWindow.Show()`.

---

## WinUI 3 migration (v2.0 — the app moved off WinForms)

The UI was rewritten in **WinUI 3 / Windows App SDK** (unpackaged, `WindowsPackageType=None`) to
match the sibling LenovoTray app (since renamed **ChargeKeeper**): Mica dashboard, tray icon via **H.NotifyIcon.WinUI**, per-VM
control cards. The network/Hyper-V core (`NetworkMonitor`, `AdapterMatcher`, `HyperVManager`,
`ConfigManager`, `ProcessRunner`, `StartupManager`) ported **unchanged** — it was already UI-agnostic.

Gotchas hit (and how they're handled):
- **The `.pri` resource index.** An unpackaged WinUI publish must ship `<App>.pri` next to the exe,
  or `Microsoft.UI.Xaml.dll` throws a stowed exception **0xC000027B** at startup. The `.csproj` has
  a `CopyAppPriToPublish` target; `installer\build-installer.ps1` verifies it landed.
- **WinForms can't host a WinUI 3 window**, so this was a full UI migration, not a bolt-on. A hidden
  `MainWindow` keeps the app alive while only the tray icon + popup are visible.
- **Native tray menu caveat (H.NotifyIcon):** the right-click menu is rebuilt as a native Win32 menu
  each time; XAML `Click`/`Opening` events do NOT fire — bind `Command` (`RelayCommand`) instead, and
  resync dynamic items in `TrayMenu.RefreshState()` before it opens.
- **UI thread marshaling** is now `DispatcherQueue.TryEnqueue` (was `SynchronizationContext.Post`).
  `NetworkMonitor.SwitchApplied` still fires on a background thread.
- **No `MessageBox`** in WinUI — small `MessageBoxW` P/Invokes in `NativeMethods` cover errors/confirms.
- **Publish is a folder, not a single file.** `PublishSingleFile`/`EnableCompressionInSingleFile`
  don't apply; the per-user Inno Setup installer copies the folder (config.json installed
  `onlyifdoesntexist` so user edits survive upgrades). `PublishTrimmed=false`
  (WinUI + reflection-y JSON trim poorly) — so reflection-based `System.Text.Json` is kept;
  `PublishReadyToRun=true` on Release for faster startup.
- **Dashboard polling** (CPU/mem/VHD) originally ran on a `DispatcherTimer` polling `Get-VM` only
  between `Activated` and hide/close. **Superseded 2026-06-29** — see the WMI section below;
  the zero-idle-when-closed property is preserved via `VmService.Subscribe/UnsubscribeMetrics`.

---

## Native WMI VM interaction (2026-06-29 — replaces PowerShell polling for VM status/power)

**Why:** the dashboard polled `Get-VM` (via `HyperVManager`'s PowerShell worker) every second
while open, and a power-button click just fired the cmdlet, waited a fixed 1200 ms, then re-polled
— no immediate feedback, no real progress, and a failure (e.g. "not enough memory to start") was
silently swallowed. Hyper-V Manager (MMC) itself doesn't poll: it uses WMI push events and
async jobs. This migrates VM status/metrics/power/guest-IPs onto that same model.

**What moved where:**
- `Services/VmService.cs` (new) — VM status, CPU/mem/uptime/VHD metrics, guest IPs, and the five
  power actions (Start/Resume/Pause/Save/Shutdown), all via `System.Management` against
  `root\virtualization\v2`.
- `Services/HyperVManager.cs` (stripped) — kept ONLY `ApplySwitchAsync`, `UpdateSwitchBindingAsync`,
  `RepairHostVNicAsync` and the PowerShell worker that backs them. These stay on PowerShell
  deliberately: they're low-frequency, background-thread, and safety-critical host-networking
  code (see gotchas #1–#7 above) — not worth the WMI rewrite risk in this pass. A later pass could
  move them too (`Msvm_VirtualEthernetSwitchManagementService`), tracked as a separate task.

**Mechanism (mirrors MMC):**
- **State** is authoritative from `Msvm_ComputerSystem.EnabledState`, pushed via a
  `ManagementEventWatcher` on `__InstanceModificationEvent` — the dashboard updates within ~1–2 s
  of an external change (e.g. starting the VM from Hyper-V Manager itself), not on a fixed tick.
- **Metrics** (CPU/mem/uptime/StatusDescriptions) come from ONE batched
  `Msvm_VirtualSystemManagementService.GetSummaryInformation` call covering every VM, once per
  tick — same idea as the old batched `GetVmDashboardAsync`, just over WMI. This only runs on a `PeriodicTimer`
  **while the dashboard is subscribed** (`SubscribeMetrics`/`UnsubscribeMetrics`, ref-counted) —
  a closed dashboard still costs nothing.
- **Power actions** call `RequestStateChange` (or `Msvm_ShutdownComponent.InitiateShutdown` for
  graceful shutdown); a return of `4096` means a job started, tracked via `Msvm_ConcreteJob`
  (`JobState`/`PercentComplete`/`ErrorDescription`) on a short poll loop. `VmService.BeginPowerAction`
  is synchronous and non-blocking: it raises an optimistic "Requesting start…" before returning,
  then live "Saving (47%)…", then success or the exact WMI failure text — surfaced in
  `DashboardWindow` as an overlay on the card's state label (`ApplyOverlay`/`_op` dictionary),
  which also disables that card's buttons while the op is in flight.

**Threading:** `System.Management` is MTA; the WinUI UI thread is STA. **Nothing in `VmService`
runs on the UI thread** — `RefreshCore` (all WMI reads) is called only from background threads
(the metrics `PeriodicTimer`, the event watcher, or `RefreshOnceAsync`'s `Task.Run`), and is
wrapped in a `lock (_refreshLock)` because those three entry points can otherwise fire
concurrently and race on the plain (non-concurrent) `Dictionary` caches — found and fixed during
review; don't remove that lock when touching `RefreshCore`.

**⚠️ Flagged assumptions — status after live-host probing (2026-07-03: one-off elevated
PowerShell probe on the real host, calling `GetSummaryInformation` both via CIM and via a
System.Management replica of `ReadSummaries`, cross-checked against `Get-VM`):**
- **[FIXED 2026-07-03, pending live re-test] CPU/memory stuck at 0: WQL projections containing
  the system property `__PATH` return ZERO rows — silently.** Verified on the live host: the
  former query
  `SELECT __PATH FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemType='Microsoft:Hyper-V:System:Realized'`
  enumerated **0 instances** through System.Management (no exception, no error — just an empty
  result), while the same class+filter via `SELECT *` returned all 3 VMs. `ReadSummaries` then hit
  `if (paths.Count == 0) return result;` and returned an empty dict → every VM's metrics defaulted
  to 0. **No log entry fired anywhere** because nothing threw — this is why two rounds of
  RequestedInformation-code fixes appeared to change nothing (the call was never reached). The
  codes `1,101,103,105,111` were already correct — confirmed against the live provider (see
  values below).
  **Same bug pattern was in `ReadSwitchNames`** (`SELECT ElementName, __PATH FROM
  Msvm_VirtualEthernetSwitch`) — matched the dashboard showing "—" for the switch name. UI was the
  differential diagnostic: every field fed by a `__PATH`-projecting query was blank/zero (CPU,
  Mem, switch name); every field from a plain-property query worked (state, guest IP, VHD size).
  **The fix (implemented in `VmService.cs`):**
  1. `ReadSummaries`: dropped the setting-data query entirely — calls `GetSummaryInformation` with
     `SettingData` = `Array.Empty<string>()` (proven live: returns all VMs with real metrics keyed
     by `ElementName`). Also checks `ReturnValue` and logs a warning on non-zero, and warns if the
     result comes back empty.
  2. `ReadSwitchNames`: no longer projects `__PATH` in WQL — uses `SELECT *` and takes the path
     from `ManagementObject.Path.Path` instead.
  3. Both `GetSummaryInformation`'s zero-row case and its non-zero `ReturnValue` case now log at
     `LogWarning` — this failure mode was otherwise 100% silent.
  **Not yet re-verified live** — the fix was validated by a standalone elevated probe script, not
  by rebuilding the actual app; confirm CPU%/Mem populate and the switch name shows on the next
  live test.
  **Code-review pass (2026-07-04) on this fix** found and corrected: `ReturnValue` is now read via
  `Convert.ToUInt32` (matching `RequestStateChange`/`InitiateShutdown` elsewhere in this file)
  instead of `SafeInt`, whose silent catch-to-0 could have masked a genuine large error code as
  success; the "no VMs"/"non-zero ReturnValue" warnings now fire once per degraded streak
  (`WarnOnce`) instead of every ~2.5 s tick; `ReadSwitchNames` gained the same empty-result warning
  as `ReadSummaries` — this doubles as the safety net for the still-unverified `Path.Path` vs.
  `HostResource` string-format assumption below, since a mismatch there will now warn instead of
  silently reading blank. Left open (documented, not fixed): the `Path.Path`/`HostResource` format
  equality itself is still unverified live; `LogDebug`→`LogWarning` was bumped in this file only,
  not in `NetworkMonitor.cs`/`HyperVManager.cs` which still have invisible `LogDebug` exception logs.
  **Live-confirmed values** (vDev-2026 running, cross-checked against `Get-VM`):
  `ProcessorLoad` = uint16 % (2); `MemoryUsage` = uint64 **MB** (14202, == Get-VM
  MemoryAssigned); `UpTime` = uint64 **ms** (1845737 ≈ Get-VM Uptime 30:48) — note class property
  is spelled `UpTime`, but WMI property access is case-insensitive so reading "Uptime" works;
  `StatusDescriptions` = ["Operating normally"]; `EnabledState` 2=Running / 3=Off ✓. Off VMs
  return NULL `ProcessorLoad`/`MemoryUsage` and `UpTime`=0, so the null→0
  `SafeInt`/`SafeLong`/`SafeULong` defaults are correct for them.
- ~~`MemoryUsage` in MB / `Uptime` in ms~~ — **VALIDATED 2026-07-03** via the probe (values
  matched `Get-VM` exactly). `WmiVmMapper.BuildStatus` conversions are correct as written.
- **`RequestStateChange` codes — FIXED, partly live-tested (2026-07-13).** The earlier assumption
  below — that `RequestedState` and `EnabledState` "share the same value space" — was WRONG and
  caused a bug: passing EnabledState codes (32768 Pause / 32769 Save) as the `RequestedState`
  returned **32775 / 0x8007 "Invalid state for this operation"**. `RequestedState` is a DISTINCT
  enum. The docs' `Saving` (32773) / `Pausing` (32776) / `Resuming` (32777) are marked **"Hyper-V V1
  only"** and a V2 host (`root\virtualization\v2`) rejects them (32773 and 32779/FastSaved both
  failed on the live host). The RequestedState values a V2 host ACCEPTS (verified against Microsoft's
  own `fsharplu` `ManagementHypervisor.fs`): **Start/Resume = Enabled (2), Pause = Quiesce (9),
  Save = Offline (6)** — Save then leaves the VM at EnabledState 32769 (Suspended). Pause and
  Shutdown are confirmed live; Save (6) still needs a live click test. NOTE the read side is
  separate and unchanged: a VM's CURRENT `EnabledState` is 32768 (host critical-pause) / **9**
  (user pause / Quiesce) / 32769 (Saved) — `WmiVmMapper.MapState` maps both 9 and 32768 → "Paused".
- Every child-settings class (memory, storage, network port, guest IP) is matched back to its
  owning VM by checking whether the setting's `InstanceID` *contains* the VM's
  `Msvm_ComputerSystem.Name` GUID (`VmService.MatchVm`). If a VM's memory/VHD/switch/IP field
  stays empty while its state/CPU update fine, this matching assumption is the first thing to
  check (log a raw `InstanceID` next to the VM GUID and compare).

If any of the above turns out wrong, the fix is confined to `WmiVmMapper` (pure, unit-tested) or
the one `VmService` read method involved — nothing downstream needs to change.

---

## Phase 2 — switch binding on native WMI (issue #17, branch `wip/wmi-switch-binding`) — ⚠️ UNVALIDATED PROTOTYPE

**Never executed against a live host.** This is the deliberately-deferred, safety-critical rewrite
of `HyperVManager`'s last three PowerShell operations to native WMI (`Msvm_VirtualEthernetSwitchManagementService`
/ `Msvm_VirtualSystemManagementService` under `root\virtualization\v2`), eliminating the
`powershell.exe` worker + Base64 protocol entirely. Public contract unchanged
(`ApplySwitchAsync`/`UpdateSwitchBindingAsync`/`RepairHostVNicAsync`, `HostVNicState`, SKIP/bound/
repaired/reshared semantics), so the rest of the app is untouched.

- **How the atomic-bind invariant (#3/#4 above) is preserved:** the re-home does a **single
  `ModifyResourceSettings`** on the switch's *external* port allocation (`HostResource` → new
  `Msvm_ExternalEthernetPort`) and **never removes or disables the internal/management-OS port
  allocation**, so there is no window in which a mid-sequence failure strands the host — the same
  guarantee the single atomic `Set-VMSwitch -NetAdapterName … -AllowManagementOS $true` gave.
- **How the duplicate-vNIC repair (#3) maps to WMI:** count of internal EPASDs (`HostResource` →
  `Msvm_ComputerSystem`) is the host-vNIC count; the repair removes the EXTRAS (keeping one) rather
  than the PowerShell drop-all-then-recreate-one, so it never dips to zero host vNICs.
- **Before this can replace the PowerShell path or merge:** it must pass every scenario in
  `docs/wmi-switch-binding-test-protocol.md` on a disposable host, and the open uncertainties U1–U7
  there (in-place `HostResource` modify, `GetText` format, device-vs-EPASD duplicate mapping, host
  `Msvm_ComputerSystem` selection, adapter→external-port mapping) must be resolved live. The
  port-classification logic is extracted to the pure, unit-tested `SwitchWmiHelpers`.

---

## Resource notes (updated 2026-06-04 — WinUI 3 figures)

**Idle CPU ~0** — the core is still fully event-driven (`NetworkChange` + `FileSystemWatcher`).
The dashboard polling timer runs **only while the dashboard is open**, so a minimised/closed app
costs nothing.  The WinUI 3 runtime raises the memory baseline versus the old WinForms build:

| Measure | WinForms v1 | WinUI 3 v2 |
|---|---|---|
| Working set (idle) | ~62 MB | ~148 MB |
| Private memory | ~13–16 MB | n/a (WinUI allocates differently) |
| Idle CPU | ~0 | ~0 |

| Lever | Verdict |
|---|---|
| `InvariantGlobalization=true` | **Kept.** ICU not loaded; no localized UI, all formatting is ordinal/invariant. |
| Remove unused `Logging.Console` pkg | **Kept.** Only the custom file sink is wired up. |
| `EnableCompressionInSingleFile` | **N/A for WinUI.** (Was rejected for WinForms: −63 MB disk but +40 MB RAM.) |
| `PublishTrimmed` | **Rejected.** WinUI 3 + reflection-based JSON trim poorly and break at runtime. |
| `PublishReadyToRun=true` | **Kept** (Release only) for faster startup. |
| `WindowsAppSDKSelfContained=true` | **Required.** Bundles the Windows App SDK so no separate runtime install is needed on the target machine. |

---

## Testing / ops cautions (for whoever runs this next)

- **Don't kill the app mid-rebind** (~25 s window) — it can strand the host adapter (#4).
- **Launch the elevated exe in the foreground.** `Start-Process` from a background/non-interactive
  context gets its UAC prompt auto-cancelled ("operation was canceled by the user").
- **Don't do unprompted elevated host-network surgery.** Reconfiguring a live `Set-VMSwitch` is
  the user's call; offer the commands or the Hyper-V Manager steps instead.
- `config.json` rules match on **MAC + CIDR**. WiFi on the same subnet as a bridged cable will
  **not** match a cable rule (different MAC) — that's intended; it falls back to NAT.
- Verify a healthy bridge with: one `Up` `vEthernet (Bridged)` carrying the LAN IP, and no
  numbered `vEthernet (Bridged) N` siblings.
- **Automated tests** (`dotnet test`) cover the pure logic only — CIDR/MAC matching, `VmStatus`
  maths, `WmiVmMapper` (EnabledState→state, MB/ms conversions, progress messages, percent parsing),
  and the `config.json` contract. The `Tests/` project **links** those source files (no
  ProjectReference to the WinUI app), so it runs without the Windows App SDK runtime. The
  UI/Hyper-V/WMI layers have no automated coverage — exercise them by building and running the app.
- **First real run after the WMI migration:** watch Task Manager while the dashboard is open — no
  `powershell.exe` should spawn for VM status/metrics anymore (only for switch binding, on network
  change). Confirm a card updates within ~1–2 s of a state change made from Hyper-V Manager (not
  this app), confirm Start on an over-provisioned VM surfaces the real WMI failure text on the
  card, and see the flagged assumptions above if anything reads as zero/blank/swapped.
