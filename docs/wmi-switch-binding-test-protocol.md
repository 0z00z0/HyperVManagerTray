# WMI switch-binding validation protocol (issue #17, Phase 2)

> **Status: UNVALIDATED prototype.** The WMI rewrite in `Services/HyperVManager.cs` on branch
> `wip/wmi-switch-binding` has **never been executed against a live host.** This document is the
> gate it must pass on a **disposable Hyper-V host** before it may replace the PowerShell path or
> merge to `master`. **Do not run this on your primary working laptop** — the failure mode of every
> scenario here is *"the host loses all networking."*

## Why this ceremony exists (read first)

`Set-VMSwitch` (the cmdlet this replaces) bundles ordering + rollback semantics that a hand-rolled
WMI sequence must reproduce exactly. Two hard-won lessons from `DEVELOPMENT_NOTES.md` drive every
scenario below:

1. **The atomic-bind rule (gotcha #3/#4).** A two-step `AllowManagementOS $false → $true` toggle
   orphaned host management vNICs and, if the process died between the two steps, left the host
   adapter with **no vNIC and no IP.** The shipping PowerShell path fixed this with a single atomic
   `Set-VMSwitch -NetAdapterName … -AllowManagementOS $true`. **The WMI re-home must never remove or
   disable the internal (management-OS) port allocation.** It only re-points the *external* port
   allocation's `HostResource`, in one `ModifyResourceSettings` call, leaving the management vNIC
   untouched — so no failure window can strand the host.
2. **The duplicate-vNIC bug (gotcha #3).** A dock undock/redock could leave the switch with a
   **duplicate** host vNIC that carries the **same MAC** as the original and is `Up` on an APIPA
   (`169.254.x.x`) address, which black-holes host egress while the VM stays fine.
   `RepairHostVNicAsync` must collapse it back to exactly one.

The WMI model (see the class doc in `HyperVManager.cs`): a switch's connections are
`Msvm_EthernetPortAllocationSettingData` (EPASD) instances. `HostResource[0]` →
`Msvm_ExternalEthernetPort`/`Msvm_WiFiPort` = external uplink; `HostResource[0]` →
`Msvm_ComputerSystem` = internal/management-OS vNIC; `Parent` → `Msvm_SyntheticEthernetPortSettingData`
= VM NIC connection.

---

## 0. Test host prerequisites

- A **throwaway** Windows 11 / Server machine with Hyper-V enabled that you can lose networking on
  and recover via console (VM console, iLO/iDRAC, or physical keyboard — **not** RDP/SSH, which die
  with the host network).
- **Two distinct physical NICs** (or one onboard NIC + one USB-Ethernet dongle) so "re-home to a
  different physical NIC" is testable. A dock with a passthrough NIC reproduces the dock scenario best.
- At least one test VM with one synthetic NIC.
- An **out-of-band recovery path** confirmed working *before* you start (see §7).
- A snapshot / checkpoint of host networking config, and note the baseline:
  ```powershell
  Get-VMSwitch | Format-Table Name,SwitchType,NetAdapterInterfaceDescription,AllowManagementOS
  Get-VMNetworkAdapter -ManagementOS | Format-Table Name,SwitchName,MacAddress,Status
  Get-NetAdapter | Format-Table Name,InterfaceDescription,Status,MacAddress
  ```

### Build the prototype (do NOT run the app yet)
```powershell
cd <worktree>
dotnet build HyperVManagerTray.csproj -c Release -r win-x64 --nologo   # expect 0 errors
dotnet test  Tests/HyperVManagerTray.Tests.csproj -c Debug --nologo    # expect all green
```

### Observation helpers (keep a second elevated console open)
```powershell
# H1 — full switch + host-vNIC + egress snapshot (run before/after every step)
function Show-NetState {
  Get-VMSwitch | ft Name,SwitchType,NetAdapterInterfaceDescription,AllowManagementOS
  Get-VMNetworkAdapter -ManagementOS | ft Name,SwitchName,MacAddress,Status,IPAddresses
  Get-NetIPAddress -AddressFamily IPv4 | ? {$_.IPAddress -like '169.254.*'} | ft InterfaceAlias,IPAddress  # APIPA = bad
  Find-NetRoute -RemoteIPAddress 8.8.8.8 | select -First 1 InterfaceAlias,NextHop
  Test-Connection 8.8.8.8 -Count 2 -Quiet   # host egress: $true = healthy
}

# H2 — raw WMI view the app acts on (external vs internal port allocations per switch)
function Show-SwitchPortsWmi($switchName) {
  $ns='root\virtualization\v2'
  $sw = gwmi -ns $ns -class Msvm_VirtualEthernetSwitch -Filter "ElementName='$switchName'"
  $ports = $sw.GetRelated('Msvm_EthernetSwitchPort','Msvm_SystemDevice',$null,$null,$null,$null,$false,$null)
  foreach ($p in $ports) {
    $eps = $p.GetRelated('Msvm_EthernetPortAllocationSettingData','Msvm_ElementSettingData',$null,$null,$null,$null,$false,$null)
    foreach ($e in $eps) {
      $hr = $e.HostResource; $cls = if ($hr) { ([wmi]$hr[0]).__CLASS } else { '(none)' }
      "{0,-40} host={1}" -f $p.ElementName, $cls
    }
  }
}
```

Watch the app's log while testing (native WMI errors surface here now, not a PowerShell transcript):
`%LOCALAPPDATA%\HyperVManagerTray\logs\` (or wherever `FileLogger` writes on the test host).

---

## Scenario A — Connect a VM NIC to a switch (`ApplySwitchAsync`)

Lowest-risk path; validate it first. Does **not** touch host networking.

1. VM NIC currently disconnected (or on a different switch). Run `Show-NetState`.
2. Drive `ApplySwitchAsync(vm, nic, switchA)` — e.g. via the tray "manual override" menu, or a tiny
   harness that news up `HyperVManager` and calls the method. **Observe:**
   - `Get-VMNetworkAdapter -VMName <vm>` → `SwitchName` == `switchA`.
   - Guest keeps/gains connectivity on `switchA`'s network.
   - Log: `Switch applied: <vm> → switchA`.
3. **Idempotency / SKIP:** invoke the same call again. **Expect** log `already on 'switchA' — no
   reconnect` and **no** guest-network blip (watch a continuous `ping` from inside the guest — zero
   dropped packets).
4. **Re-point:** call `ApplySwitchAsync(vm, nic, switchB)`. NIC moves to `switchB`; one expected brief
   guest blip. Confirm `SwitchName` == `switchB`.
5. **Never-connected path:** on a NIC that has never been attached to any switch, confirm the
   `AddResourceSettings` branch connects it (not just the modify branch). Check the log doesn't throw
   "Default Ethernet Connection setting-data template not found".

**Pass:** connect, SKIP-no-blip, and re-point all behave as above. **Fail** → do not proceed to the
host-networking scenarios.

---

## Scenario B — Bind an Internal/Private switch to a physical NIC (`UpdateSwitchBindingAsync`, first bind)

1. Create/choose a switch that is **Internal or Private** (no external uplink). `Show-NetState` +
   `Show-SwitchPortsWmi <switch>` — expect an internal port allocation but no external one.
2. Drive `UpdateSwitchBindingAsync(switch, <alias of NIC-1>)` where `<alias>` is the Windows
   connection name (e.g. `Ethernet`) exactly as `AdapterMatcher` would pass it.
3. **Observe (host must stay online throughout):**
   - `Get-VMSwitch <switch>` → `SwitchType` == `External`, `AllowManagementOS` == `True`,
     `NetAdapterInterfaceDescription` == NIC-1's description.
   - `Show-SwitchPortsWmi` now shows **one external** (`Msvm_ExternalEthernetPort`) + **one internal**
     (`Msvm_ComputerSystem`) allocation — **not two internal**.
   - `Get-VMNetworkAdapter -ManagementOS -SwitchName <switch>` → **exactly one** vNIC.
   - `Show-NetState`: no `169.254.*` APIPA address; `Find-NetRoute 8.8.8.8` egresses via
     `vEthernet (<switch>)`; `Test-Connection 8.8.8.8` == `$true`.
   - Log: `Switch '<switch>' bound to '<alias>'`.
4. **SKIP:** invoke the identical bind again → log `already bound to '<alias>' — no rebind`, and **zero**
   host-egress interruption (keep a `ping 8.8.8.8 -t` from the host running; expect no drops).
5. **Adapter absent:** call with an alias that isn't currently present (unplug NIC-2 first) → log
   `Adapter '…' not present — switch left unchanged`, switch untouched.

**Pass:** external+one-internal end state, host stays online, SKIP is a true no-op.

---

## Scenario C — Re-home the external switch to a *different* physical NIC (`UpdateSwitchBindingAsync`, the risky one)

This is the exact operation that historically orphaned host vNICs. **This is the scenario to trust
least — see Uncertainties U1/U2.**

1. Start from Scenario B's end state (switch External, bound to **NIC-1**, one host vNIC). Record the
   host vNIC's MAC.
2. Drive `UpdateSwitchBindingAsync(switch, <alias of NIC-2>)` (a different physical adapter, ideally on
   a different LAN so a wrong result is obvious).
3. **Observe:**
   - `NetAdapterInterfaceDescription` flips to NIC-2's description; `AllowManagementOS` stays `True`.
   - `Show-SwitchPortsWmi`: still exactly **one external + one internal** allocation. The external one
     now points at NIC-2's `Msvm_ExternalEthernetPort`.
   - `Get-VMNetworkAdapter -ManagementOS -SwitchName <switch>` → **still exactly one** host vNIC
     (**this is the regression check** — the old two-step toggle produced a second one here).
   - No APIPA; host egress recovers on NIC-2's network within a few seconds (`Test-Connection` `$true`).
   - VM connected to the switch follows to NIC-2's network.
4. **Repeat the re-home 5× back and forth (NIC-1 ↔ NIC-2).** After each, assert
   `Get-VMNetworkAdapter -ManagementOS -SwitchName <switch>` count **stays 1** and no `vEthernet
   (<switch>) 2/3/…` siblings appear. Cumulative orphaning is the classic failure — it only shows up
   after several cycles.

**Pass:** every re-home keeps exactly one host vNIC and host egress recovers. **If a second host vNIC
ever appears, or the modify errors (e.g. 0x8007 / provider rejects changing `HostResource` on an
existing external allocation), stop — see U1.**

---

## Scenario D — Dock undock/redock: reproduce & repair the duplicate-vNIC bug (`RepairHostVNicAsync`)

Recreates gotcha #3 and validates the collapse.

1. From an External, bound switch, **physically undock / unplug** the docking NIC, wait ~10 s, then
   **redock / replug.** Let the app's `NetworkMonitor` fire its normal evaluate→bind→repair, **or**
   drive `UpdateSwitchBindingAsync` (which calls the repair) then `RepairHostVNicAsync(switch)` directly.
2. **Induce the bug if a natural redock doesn't:** the historical trigger is a rebind that leaves two
   host vNICs. If `Get-VMNetworkAdapter -ManagementOS -SwitchName <switch>` shows **> 1** (often the
   dead one is `Up` with a `169.254.*` address and shares the live one's MAC), you've reproduced it.
   `Show-NetState` should show the APIPA address and likely `Find-NetRoute 8.8.8.8` egressing via the
   wrong `vEthernet` — host "randomly" offline.
3. Drive `RepairHostVNicAsync(switch)`. **Observe:**
   - Log: `Collapsed duplicate host vNIC(s) on switch '<switch>' to one (was N)`.
   - `Get-VMNetworkAdapter -ManagementOS -SwitchName <switch>` → **exactly one**.
   - APIPA address gone; `Find-NetRoute 8.8.8.8` egresses via `vEthernet (<switch>)`;
     `Test-Connection 8.8.8.8` == `$true` within a few seconds.
   - Return value == `Repaired`.
4. **Reshare path:** manually turn off sharing (`Set-VMSwitch <switch> -AllowManagementOS $false`) so
   there are **zero** host vNICs, then drive `RepairHostVNicAsync`. Expect return `Reshared`, one host
   vNIC restored, host back online.
5. **Healthy no-op:** with exactly one host vNIC, drive `RepairHostVNicAsync` → return `Ok`, **no
   change, no blip** (host `ping -t` shows zero drops).

**Pass:** duplicate collapses to one and egress returns; reshare restores; healthy is a true no-op.
**See U3** — whether a Windows-device-level duplicate always corresponds to a second *internal EPASD*
(what the WMI repair counts and removes) is the key unknown here. If the duplicate persists after a
`Repaired` result, U3 is confirmed and the repair needs the full drop-all-then-recreate fallback (§7).

---

## Scenario E — Kill the process mid-rebind (crash safety)

Validates the atomic-bind invariant: a hard kill during a re-home must **never** leave the host with
no management vNIC.

1. Start a re-home (Scenario C). While the WMI call is in flight (re-homing can take tens of seconds),
   **hard-kill** the app: `Stop-Process -Name HyperVManagerTray -Force` (or kill the test harness).
   Do several kills at different moments across repeated runs to hit different points in the sequence.
2. **Observe immediately after each kill:**
   - `Get-VMNetworkAdapter -ManagementOS -SwitchName <switch>` → **≥ 1** host vNIC at all times
     (**never zero** — this is the whole point).
   - Host retains an IP (not APIPA) on the switch or its previous NIC; `Test-Connection 8.8.8.8`
     recovers without manual intervention.
   - Worst acceptable outcome: the *external* uplink is mid-change (egress briefly down) but the
     management vNIC still exists, so re-launching the app and letting it re-bind restores egress. An
     **unacceptable** outcome is zero host vNICs / stranded host adapter.
3. Re-launch the app; confirm it converges to External + one host vNIC + egress.

**Pass:** no kill instant produces a zero-host-vNIC / no-IP host. **Fail** → the sequence is not
actually atomic; revert to PowerShell.

---

## 6. Regression / longevity pass

- Leave the app running through **20+ real network transitions** (dock/undock, Wi-Fi↔Ethernet,
  VPN up/down). Periodically assert: each rule switch has exactly one host vNIC, no numbered
  `vEthernet (<switch>) N` siblings, no APIPA, host egress healthy.
- Confirm **no `powershell.exe` ever spawns** from the app during any of this (Task Manager /
  `Get-CimInstance Win32_Process -Filter "ParentProcessId=<app pid>"`). That's the payoff of #17.
- Confirm idle: no leaked WMI/COM handles growing over time (`Get-Process HyperVManagerTray` handle
  count stable).

---

## 7. Rollback & recovery (keep this open in a console before you start)

If the host loses networking during any scenario, recover from the **out-of-band console**:

```powershell
# Re-enable host sharing on the switch (recreates a single clean management vNIC):
Set-VMSwitch -Name '<switch>' -AllowManagementOS $true

# Full duplicate-vNIC cleanup (the manual recipe from DEVELOPMENT_NOTES #3):
Set-VMSwitch -Name '<switch>' -AllowManagementOS $false
Get-NetAdapter | ? { $_.Name -like 'vEthernet (<switch>)*' } | ForEach-Object {
    pnputil /remove-device $_.PnPDeviceID   # remove every leftover host vNIC device
}
Set-VMSwitch -Name '<switch>' -AllowManagementOS $true   # recreate exactly one, fresh MAC

# Nuclear option — detach the switch from the physical NIC entirely, restoring the NIC's own IP:
Set-VMSwitch -Name '<switch>' -SwitchType Internal
# or rebind by hand to a known-good adapter:
Set-VMSwitch -Name '<switch>' -NetAdapterName '<good alias>' -AllowManagementOS $true
```

If a switch is wedged, `Remove-VMSwitch -Name '<switch>' -Force` and recreate it. Because this is a
disposable host, reverting to the pre-test checkpoint is always acceptable.

---

## 8. Open uncertainties to resolve during testing (be honest — this code is unproven)

These are the specific places the implementation is a best-effort reading of the WMI model rather than
a validated fact. Each **must** be confirmed or corrected during the run:

- **U1 — Can `ModifyResourceSettings` change an external allocation's `HostResource` in place?**
  Scenario C re-homes by editing the existing external EPASD's `HostResource` and calling
  `ModifyResourceSettings`. It is *possible* the provider rejects an in-place endpoint change and
  requires **remove-then-add** of the external port instead. If C fails at the modify, switch to:
  `RemoveResourceSettings(old external EPASD)` then `AddResourceSettings(new external EPASD)` — but
  note that remove-then-add of the *external* port has a brief no-egress window (the internal/management
  vNIC still exists, so the host keeps an IP; it just loses uplink until the add lands). The internal
  port must still never be touched.
- **U2 — `GetText` format for the embedded instances.** Add/Modify pass
  `settingData.GetText(TextFormat.WmiDtd20)`. The Microsoft VM-NIC-connect blog used WmiDtd20 (`2`) and
  it worked; some samples use `CimDtd20`. If Add/Modify returns an "invalid parameter"/parse error,
  try `TextFormat.CimDtd20`.
- **U3 — Does a device-level duplicate host vNIC map to a second *internal EPASD*?** The repair counts
  and removes internal EPASDs (`HostResource` → `Msvm_ComputerSystem`). If a dock-induced duplicate
  shows as two host vNIC **devices** but only **one** EPASD, `RepairHostVNicCore` will report `Ok` and
  not fix it. If Scenario D shows the duplicate persisting after a repair, fall back to the PowerShell
  recipe's semantics: remove **all** internal EPASDs then add exactly one back (accepting the brief
  zero-vNIC window, mitigated by doing it fast and under the lock). Decide based on live evidence.
- **U4 — Host `Msvm_ComputerSystem` selection.** `AddInternalPort` targets the host as the single
  `Msvm_ComputerSystem` whose `Caption <> 'Virtual Machine'`. Confirm exactly one such instance exists
  on the test host and it's the right one (it should be the host itself). If ambiguous, filter by
  `Caption='Hosting Computer System'` or `Name = <host NetBIOS>`.
- **U5 — External adapter → `Msvm_ExternalEthernetPort` mapping.** We match by MAC
  (`PermanentAddress`), then adapter description (`ElementName`). Verify the target adapter actually
  appears as an `Msvm_ExternalEthernetPort` with a matching `PermanentAddress`; teamed/USB/Wi-Fi
  adapters can differ. Wi-Fi surfaces as `Msvm_WiFiPort` (classified external) — confirm if a Wi-Fi
  rule is in scope.
- **U6 — `Msvm_SettingsDefineState` returns the realized (not a snapshot) settings.** `FindVmSettings`
  and `SwitchSettings` take the first related settings via `Msvm_SettingsDefineState`. Confirm on a VM
  that *has checkpoints* that the connect still targets the active configuration.
- **U7 — `AddResourceSettings` `AffectedConfiguration` as a path string.** We pass
  `settings.Path.Path` for the reference parameter. If the provider rejects it, pass the
  `ManagementObject` / a `__PATH` reference form instead.

Only once **every** scenario passes and **U1–U7** are resolved should this branch be considered for
merge. Until then it stays a prototype: **unvalidated, never run live.**
