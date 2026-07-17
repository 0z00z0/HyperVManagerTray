# Hyper-V Manager Tray

A Windows system-tray application that automatically connects your Hyper-V virtual machine to the correct virtual network switch based on which physical network the host is connected to.

> ### 🤖 100% vibe coded
> This project was written **entirely** by an AI agent ([Claude](https://www.anthropic.com/claude), via Claude Code) through conversational prompts — design, implementation, debugging, deployment scripts, and this README. No line of the source was hand-written by a human. It's shared as-is, as an experiment in what end-to-end "vibe coding" produces. Read the code with that in mind, and review before relying on it.

---

## What it does

When you move between networks — office LAN, home Wi-Fi, mobile hotspot — the virtual machine needs a different network connection:

| Host network | VM should use |
|---|---|
| Office LAN (`10.0.0.0/23`, adapter `AA:BB:CC:DD:EE:FF`) | **Bridged** switch (full LAN access) |
| Anything else | **Default Switch** (NAT, always works) |

The app watches for network changes in the background. The moment the host connects to a recognised network, the VM's NIC is automatically reconnected to the right Hyper-V virtual switch. If no rule matches, it falls back to the configured fallback switch (the Hyper-V **Default Switch** on a fresh install). All Hyper-V interaction — switch binding, VM NIC reconnects, VM status and power — goes through the native Hyper-V WMI providers (`root\virtualization\v2`); no PowerShell is involved. When a reconnect or switch bind **fails**, the app says so rather than pretending: the tray icon turns red and a balloon reports what could not be done.

It also includes a **WinUI 3 dashboard** (left-click the tray icon) that shows the live host-network/switch status and a control card per managed VM — state, CPU / memory / VHD-size meters, and power buttons appropriate to the state. A rule can optionally **auto-start** its VMs when its network becomes active. A **Settings window** (right-click → **Settings…**) covers the whole configuration — managed VMs, network rules, fallback, adapter renaming, logging, startup — so nothing requires hand-editing a file (though that stays supported).

---

## Requirements

- Windows 11 host with **Hyper-V** enabled
- The user account must be a member of the **Hyper-V Administrators** group (or run as Administrator)
- The virtual switches your rules and fallback name must exist in Hyper-V Virtual Switch Manager (a fresh install references only the built-in **Default Switch**)
- To **run** the installed app: the **.NET 10 Desktop Runtime**. The app is published **framework-dependent** — the installer checks for the runtime and offers to install it if missing. The Windows App SDK is bundled, so no separate Windows App Runtime install is needed.
- To **build** from source: the .NET 10 SDK.

---

## Setup

1. Run or publish the application (see below). There is **nothing to edit first** — if no `config.json` sits next to the `.exe`, the app creates a blank-slate one and tells you it did.
2. A tray icon appears. **Left-click** it for the status dashboard + VM controls; **right-click** for the quick-command menu (re-check the network, a temporary switch override, manage VMs) and the **Settings** window.
3. Add your first VM from the tray's **Manage VMs** menu or from **Settings → Managed VMs**, and your rules from **Settings → Network**. Hand-editing `config.json` stays fully supported ([Configuration](#configuration) below) — the file is watched, so a saved edit applies without a restart. An edit that doesn't parse is rejected and announced with a tray balloon; the settings already loaded keep running until you fix it.

> The shipped `config.json` is deliberately **empty** — no example rule, no example VM. A sample that looks configured but targets a VM you don't have is a trap, not a starting point: it matches nothing and only warns to a log file. The annotated, fully-populated example lives under [Configuration](#configuration).

---

## Running

### Development / debug run

```powershell
dotnet run
```

> The app requires elevation (UAC prompt) because it controls Hyper-V switches.

### Install (recommended)

Via **winget** (per-user, no admin needed to install — the app elevates itself at runtime):

```powershell
winget install 0z00z0.HyperVManagerTray
winget upgrade 0z00z0.HyperVManagerTray   # later, to update
```

Or download `HyperVManagerTray-Setup-<version>.exe` from the
[latest release](https://github.com/0z00z0/HyperVManagerTray/releases/latest) and run it.

The setup offers two optional tasks:

- **Run at startup** — a `/RL HIGHEST` logon task (one UAC prompt, only if ticked) so the
  elevated app auto-starts at sign-in with no boot-time prompt.
- **Auto update in background** — a non-elevated logon task that runs `winget upgrade` 5 minutes
  after each sign-in, so you stay on the latest published version automatically.

It installs to `%LocalAppData%\Programs\HyperVManagerTray` and preserves any existing
`config.json` on upgrade.

> The app and installer are Authenticode-signed (SHA-256, timestamped) by
> `CN=ZeroZero Software`. The certificate is self-signed, so first run may still show a
> SmartScreen / "Unknown Publisher" prompt — see [`docs/SIGNING.md`](docs/SIGNING.md) to verify
> the signature or optionally trust the publisher.

### Build the installer from source

A **per-user Inno Setup installer** builds from `installer\`:

```powershell
# one-time, if Inno Setup is missing:
winget install JRSoftware.InnoSetup

.\installer\build-installer.ps1   # auto-bumps the patch version
```

This publishes the app **framework-dependent** (`--self-contained false`, with the Windows App SDK
bundled) and compiles `installer\Output\HyperVManagerTray-Setup-<version>.exe`. Keeping the .NET
runtime out of the payload keeps the installer small; the installer detects a missing **.NET 10
Desktop Runtime** on the target and offers to install it (via winget, falling back to a direct
download). See [`installer/README.md`](installer/README.md) for how the elevation is handled.

> **Releases** are automated: pushing a `vX.Y.Z` tag triggers `.github/workflows/release.yml`,
> which builds + signs the installer, patches the winget manifests, creates the GitHub Release,
> and re-validates the published manifests.

### Publish manually (no installer)

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:WindowsAppSDKSelfContained=true
```

Output folder: `bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\` (run `HyperVManagerTray.exe` from there; the `.pri` next to it is required). This matches what the installer ships: framework-dependent, so the machine needs the .NET 10 Desktop Runtime. Passing `--self-contained true` instead also works if you want a folder that runs without any runtime install — the shipped builds don't do this.

### Auto-start with Windows

**Settings → General → Run on startup** toggles auto-start at login. (This toggle used to live on the tray menu; it moved to Settings.)

Because this app requires elevation (UAC), it **cannot** auto-start from a `HKCU\...\Run` entry — Windows launches Run-key items with a standard token and silently skips apps that demand administrator rights. Instead, the toggle creates a **Scheduled Task** with "Run with highest privileges" and a logon trigger. The task runs in your interactive session, so the tray icon still appears, with no UAC prompt at logon.

The toggle is equivalent to:

```powershell
# Enable
schtasks /Create /TN "HyperVManagerTray" /TR "\"%LOCALAPPDATA%\Programs\HyperVManagerTray\HyperVManagerTray.exe\"" /SC ONLOGON /RL HIGHEST /F
# Disable
schtasks /Delete /TN "HyperVManagerTray" /F
```

**Where it's stored** — the task named `HyperVManagerTray` lives in:

| | |
|---|---|
| **Task Scheduler** | Task Scheduler Library → `HyperVManagerTray` |
| **On disk** | `C:\Windows\System32\Tasks\HyperVManagerTray` (XML definition) |
| **Registry** | `HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree\HyperVManagerTray` |

To inspect or verify it from PowerShell:

```powershell
schtasks /Query /TN "HyperVManagerTray" /V /FO LIST
```

> **Migration note:** older versions wrote a value named `HyperVManagerTray` under `HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Run`. That entry never worked for this elevated app and is now removed automatically the first time you toggle **Run on startup**.

---

## Dashboard (left-click the tray icon)

A borderless Mica popup titled **"Hyper-V Manager"** near the tray:

- **HOST NETWORK** — Adapter, IP, Gateway, DNS of the active host network, and the rule row reports the actual apply **outcome**, not just which rule matched.
- **Per-VM cards** (one per **managed** VM) — switch name and active rule shown as a subtitle; state (Running/Off/Paused/Saved, plus live transition states such as "Restoring (10%)"); CPU / memory / VHD-size meters when running; power buttons appropriate to the state: **Start**, **Shutdown**, **Pause**, **Resume**, **Save**, **Connect**, **Start & Connect**. A power action shows its progress on the card and reports failure rather than going quiet.

Metrics refresh every ~2.5 s **only while the dashboard is open**, so a closed dashboard costs no CPU.

The dashboard is the app's **only** VM power surface — the tray menu deliberately carries no power verbs (see below). A consequence worth knowing: since the cards cover managed VMs only, the app offers **no power controls for unmanaged VMs** — manage the VM first (one click in the tray's Manage VMs list) or use Hyper-V Manager.

## Context menu (right-click the tray icon)

The tray menu is the **quick-command surface**; the Settings window is the complete superset. Everything here (except Exit) is also reachable from Settings.

| Item | Description |
|---|---|
| **⬆ Update available: vX.Y.Z** | Only shown when a newer release is published — opens the GitHub releases page |
| **Re-check network now** | Re-runs rule matching, applies any change, and reports the result |
| **Override VM switch (until next network change) ▶** | Force a managed VM onto a specific switch — transient, undone by the next network change |
| **Manage VMs ▶** | Every VM on the host as a checkable list: a checkmark means this app manages it. Click an unmanaged VM to start managing it, a managed one to stop (with one confirmation) |
| **Settings…** | Opens the [Settings window](#settings-window) |
| **Check for updates** | Runs an update check now and reports the answer either way — including "you're up to date", which the badge above can never say |
| **About…** | Version, links, licences, check for updates |
| **Exit** | Stops the application |

Items from older versions didn't vanish — they moved to Settings: **Repair host networking** and the config/log actions are under Settings → Maintenance, **Add current network as bridged** is under Settings → Network, and **Run on startup** is under Settings → General.

## Settings window

Right-click the tray icon → **Settings…**. Six sections in a sidebar; every change is saved to `config.json` as you make it. The window remembers its size and position between opens.

| Section | What's there |
|---|---|
| **General** | Run on startup (the elevated scheduled task); log level for all of the app's log files |
| **Managed VMs** | The VMs this app looks after: add or remove one, the NIC name each is reconnected through, and an optional action (pause / save / shutdown, after a configurable delay) when the bridged network is lost |
| **Network** | The rules editor — add, edit, remove and re-prioritise rules; **Add current network** captures the live adapter's MAC and subnet in one step; the fallback switch and target VMs; the transient per-VM switch override |
| **Adapters** | Rename a physical adapter. Note this renames the adapter's **description** (what Device Manager, Hyper-V Manager and this app show) — not its Windows connection name/alias. Renaming briefly drops that adapter's connection; only real physical NICs are listed, and a rename can be reset to the factory name |
| **Maintenance** | Open `config.json`, open any of the three log files or the logs folder, reload the config from disk (a reload that can't parse says so and changes nothing), re-check the network, repair host networking (for the "host offline but VM online" duplicate-vNIC state after a dock cycle), check for updates |
| **About** | The same brand/about content as the About window, embedded |

---

## Configuration

`config.json` is loaded from the same directory as the executable. Everything in it can be edited from the Settings window, so hand-editing is a choice, not a requirement. It is watched for changes — edits take effect immediately without a restart. If the file is missing the app writes the blank-slate default below and carries on; if an edit doesn't parse, the app keeps the last good config, says so in a tray balloon, and re-reads the file on your next save.

The default the app ships (and self-heals to) is just this — a fallback switch and nothing else:

```json
{
  "logLevel": "Debug",
  "virtualMachines": [],
  "rules": [],
  "fallback": {
    "virtualSwitch": "Default Switch",
    "targetVms": []
  }
}
```

Everything below is the annotated **reference** for the full format — copy from it as needed:

```jsonc
{
  "logLevel": "Debug",                     // Minimum severity for ALL log files; "None" disables logging
  "virtualMachines": [
    {
      "name":                     "MyVM",             // Hyper-V VM name (exact)
      "nicName":                  "Network Adapter",  // NIC name inside Hyper-V Manager
      "onBridgeLostAction":       "pause",            // optional: "pause" | "save" | "shutdown" when the
                                                      // bridged network is lost; null/"none" = do nothing
      "onBridgeLostDelaySeconds": 30                  // wait before acting; cancelled if the bridge returns
    }
  ],
  "rules": [
    {
      "name":          "Office LAN",       // Shown in the tray status
      "priority":      1,                  // Lower = evaluated first
      "conditions": {
        "adapterMac":  "AA:BB:CC:DD:EE:FF", // Host NIC MAC (optional)
        "ipCidr":      "10.0.0.0/23"         // Host IP must fall in this range (optional)
      },
      "virtualSwitch": "Bridged",          // Hyper-V switch to connect to
      "targetVms":     ["MyVM"],           // VMs to reconnect
      "autoStart":     false               // start/resume targetVms when this rule activates
    }
  ],
  "fallback": {
    "virtualSwitch": "Default Switch",     // Used when no rule matches
    "targetVms":     ["MyVM"]
  }
}
```

The app also writes a few keys of its own into the same file — `adapterNames` (the saved original
descriptions behind the adapter-rename Reset) and `settingsWindow*` (the Settings window's last
position/size). Leave them alone; they are not configuration you need to author.

### Adding a new network rule

**Option A — from Settings:** Connect to the network, then **Settings → Network → Add current network**. The app reads the current adapter's MAC and subnet automatically. (This action used to live on the tray menu.)

**Option B — the rules editor:** **Settings → Network → Add rule** gives a blank rule to fill in by hand.

**Option C — manually:** Add an object to the `rules` array in `config.json`. Both `adapterMac` and `ipCidr` are optional; omitting both means the rule matches any active adapter.

---

## Project structure

```
HyperVManagerTray/
├─ App.xaml(.cs)            WinUI app entry point — owns services, tray icon, dashboard
├─ MainWindow.xaml(.cs)     hidden host window (keeps the app alive)
├─ Services/                UI-agnostic logic (no WinUI dependency)
│  ├─ NetworkMonitor.cs        watches NICs, debounces, drives switch changes
│  ├─ AdapterMatcher.cs        rule evaluation (MAC + CIDR), adapter selection
│  ├─ VmService.cs             VM status/metrics/power/guest IPs via native WMI (event-driven)
│  ├─ HyperVManager.cs         switch binding + host-vNIC repair via native WMI
│  ├─ SwitchWmiHelpers.cs      pure classification/matching logic behind HyperVManager
│  ├─ HostInventory.cs         one cold read of the host's VMs/switches/adapters for Settings
│  ├─ ConfigManager.cs         loads/watches/writes config.json
│  ├─ StartupManager.cs        "run at startup" scheduled task (schtasks)
│  ├─ UpdateChecker.cs         GitHub release check behind the update badge
│  ├─ ProcessRunner.cs         shared process-spawning helper (timeout, stream capture)
│  └─ FileLogger.cs            category-routing ILogger file sink (switcher/vm-power/ui logs)
├─ Models/                  POCOs: AppConfig, NetworkRule, VmTarget, VmStatus, DiscoveredVm, VmOperation
├─ UI/                      DashboardWindow (Mica popup + VM cards), SettingsWindow (sidebar + editors),
│                           TrayMenu, RenameAdapterWindow + AdapterRenameFlow, shared action classes
│                           (NetworkActions, ManagedVmActions), UpdatePrompt, UiActivityLog
├─ Helpers/                 icon generation, adapter renaming (SetupAPI), window placement,
│                           title-bar theming, UI text builders, WMI mapping, small shared utilities
├─ Tests/                   xUnit tests (links the pure Services/Models/Helpers sources)
├─ installer/              per-user Inno Setup installer (.iss + build script)
└─ config.json             blank-slate default (shipped next to the exe; also written on first run if absent)
```

## Tests

```powershell
dotnet test
```

`Tests/` is an xUnit project (745 tests) covering the pure logic — CIDR/MAC matching
(`AdapterMatcher`), the VM status maths (`VmStatus`), the `config.json` contract, the WMI
classification helpers (`SwitchWmiHelpers`, `WmiVmMapper`), log routing (`FileLogger`), and the
UI's text/state helpers. It **links** the relevant source files rather than referencing the WinUI
app, so `dotnet test` needs no Windows App SDK runtime. The windows themselves and the live WMI
calls are exercised by building and running the app, not by this suite.

## Development notes

Non-obvious Hyper-V / Windows-networking gotchas and the reasoning behind the design choices
are written up in [`DEVELOPMENT_NOTES.md`](DEVELOPMENT_NOTES.md) — read it before changing how
the app talks to Hyper-V or the host network.

---

## Built with

- **Language / UI:** C# on **.NET 10**, **WinUI 3 / Windows App SDK** (`net10.0-windows10.0.26100.0`, unpackaged, Mica backdrop)
- **OS integration:** Win32 P/Invoke (`iphlpapi.dll` `GetBestInterface`, `user32.dll`/`Shcore.dll` for popup positioning + message boxes, SetupAPI for the adapter rename); all Hyper-V interaction — VM status/metrics/power/guest IPs, switch binding, host-vNIC repair — via native WMI (`root\virtualization\v2`, `System.Management`), event-driven and PowerShell-free (metrics poll only while the dashboard is open); `schtasks.exe` for the auto-start and auto-update tasks

## External libraries

Every third-party package the app references (`HyperVManagerTray.csproj`):

| Name | Version | Author / Publisher | Purpose | License |
|---|---|---|---|---|
| [Microsoft.WindowsAppSDK](https://www.nuget.org/packages/Microsoft.WindowsAppSDK) | 2.1.3 | Microsoft | WinUI 3 framework (windowing, XAML, Mica) | MS-EULA¹ |
| [Microsoft.Windows.SDK.BuildTools](https://www.nuget.org/packages/Microsoft.Windows.SDK.BuildTools) | 10.0.28000.1839 | Microsoft | Windows SDK build tooling for the App SDK | MS-EULA¹ |
| [H.NotifyIcon.WinUI](https://github.com/HavenDV/H.NotifyIcon) | 2.4.1 | HavenDV | System-tray icon + native context menu for WinUI 3 | MIT |
| [System.Drawing.Common](https://www.nuget.org/packages/System.Drawing.Common) | 10.0.8 | Microsoft | Renders the tray `.ico` at runtime | MIT |
| [Microsoft.Extensions.Logging](https://www.nuget.org/packages/Microsoft.Extensions.Logging) | 10.0.8 | Microsoft | Logging abstraction; output goes to a small custom file sink | MIT |
| [System.Management](https://www.nuget.org/packages/System.Management) | 10.0.8 | Microsoft | WMI access (`root\virtualization\v2`) for VM status/power and switch binding — replaced the earlier PowerShell path | MIT |

¹ The NuGet packages ship under the Microsoft Software License Terms; the Windows App SDK
*source* is MIT on [GitHub](https://github.com/microsoft/WindowsAppSDK).

The only **non-Microsoft** runtime dependency is **H.NotifyIcon.WinUI** (the tray icon — used the
same way as in the sibling ChargeKeeper app).

The test project ([`Tests/HyperVManagerTray.Tests.csproj`](Tests/HyperVManagerTray.Tests.csproj))
additionally uses, at **test time only** (nothing ships in the app):

| Name | Version | Author / Publisher | Purpose | License |
|---|---|---|---|---|
| [xunit](https://github.com/xunit/xunit) | 2.9.2 | .NET Foundation & contributors | Unit-test framework | Apache-2.0 |
| [xunit.runner.visualstudio](https://github.com/xunit/visualstudio.xunit) | 2.8.2 | .NET Foundation & contributors | VSTest adapter so `dotnet test` discovers xUnit tests | Apache-2.0 |
| [Microsoft.NET.Test.Sdk](https://github.com/microsoft/vstest) | 17.11.1 | Microsoft | .NET test host / VSTest platform | MIT |

## Shared components

The **About** window comes from [0z0-shared](https://github.com/0z00z0/0z0-shared)
(`ZeroZero.Brand.WinUI.BrandAboutWindow`, MIT) — the shared components library used across
ZeroZero Software apps, referenced as a sibling-folder `ProjectReference` (no NuGet package yet).
Local builds resolve it as the sibling `..\0z0-shared` folder; CI checks the repo out into a
workspace subfolder and points the `ZeroZeroSharedDir` MSBuild property at it (see
[`.github/workflows/ci.yml`](.github/workflows/ci.yml)).

## Credits & acknowledgements

- **[H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon)** by **HavenDV** (MIT) — the WinUI 3
  tray-icon support this whole app hangs off. It continues the earlier
  [Hardcodet NotifyIcon for WPF](https://github.com/hardcodet/wpf-notifyicon) by Philipp Sumi.
- **[0z0-shared](https://github.com/0z00z0/0z0-shared)** (MIT) — ZeroZero Software's shared
  branding/components library provides the About window (see [Shared components](#shared-components)).
- **[fsharplu](https://github.com/microsoft/fsharplu)** (Microsoft, MIT) — its
  `ManagementHypervisor.fs` was the reference used to pin down the `RequestedState` values a
  Hyper-V **V2** WMI host actually accepts for `RequestStateChange` (Start/Resume = 2,
  Pause = 9, Save = 6), where the official docs list V1-only codes that a V2 host rejects.
  See [`DEVELOPMENT_NOTES.md`](DEVELOPMENT_NOTES.md). No code was copied — used as documentation.
- **Hyper-V Manager (MMC)** — the event-driven WMI model in `VmService` (push events + async
  `Msvm_ConcreteJob` tracking instead of polling) deliberately mirrors how Microsoft's own
  Hyper-V Manager behaves.

**Tooling:** the installer is built with **[Inno Setup](https://jrsoftware.org/isinfo.php)** by
Jordan Russell & Martijn Laan (free, with attribution under its license), and distributed via
**[winget](https://github.com/microsoft/winget-cli)** (Microsoft, MIT).

---

## License

[MIT](LICENSE) © ZeroZero Software — see [`LICENSE`](LICENSE) for the full text.

---

## Logging

Logs are written to `%APPDATA%\HyperVManagerTray\`, split by concern:

| File | What lands there |
|---|---|
| `switcher.log` | Switch changes, rule evaluation, network events, errors — the catch-all |
| `vm-power.log` | A begin + outcome audit line for every VM power action, with its origin |
| `ui.log` | Tray-menu commands, window open/close, rename-flow events, and interactive-path latency |

One setting governs all three: the log level in **Settings → General** (or `logLevel` in
`config.json`); `None` silences them. A crash additionally writes `crash.log` in the same folder.
All files are openable from **Settings → Maintenance**.

**Latency lines** (`ui.log`) exist to answer "why did that feel slow" with numbers rather than
impressions. The startup milestones — time from process start to the tray icon, and to the icon first
showing an established (non-grey) state — are logged at `Information`, so an ordinary boot records them.
The per-interaction lines — the tray right-click and the balloon hop — are logged at `Debug`, so set the
log level to **Debug** before reproducing a slow interaction or they will not appear.

Each line names its own boundaries, because every one of these figures is a *subset* of the delay you
actually feel and none should be read as the whole of it. In particular the right-click line reports the
app-side menu **rebuild**, which is not menu-open latency: the shell's dispatch before the handler, and
the native Win32 menu that H.NotifyIcon builds and paints after it returns, are both outside this
process and cannot be timed from within it. What the line does carry is the working set at the moment
the click arrived, and the idle gap since the previous right-click — enough to tell a paged-out process
from a slow one by comparing two consecutive opens.

---

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| UAC prompt on every launch | Normal — required for Hyper-V access. Enable **Settings → General → Run on startup** for a prompt-free elevated auto-start. |
| Status shows "Fallback" on the office LAN | MAC or CIDR in the rule does not match — check `switcher.log` |
| VM card shows "Unknown" / no CPU·memory meters | The configured VM name doesn't match a VM on the host, or the VM isn't running (only running VMs report metrics) |
| Tray icon turns red / a balloon reports a failure | A switch bind or VM reconnect failed — commonly the account lacks Hyper-V Administrator rights, or the named switch doesn't exist. Details in `switcher.log`. |
| Host has no network but the VM does (after a dock cycle) | A duplicate host vNIC — **Settings → Maintenance → Repair host networking** |
| Dashboard opens blank / `0xC000027B` at startup | The `.pri` resource index isn't next to the exe — re-run the installer or copy the whole publish folder |
| App won't start after a manual (non-installer) copy | The .NET 10 Desktop Runtime is missing — the build is framework-dependent (the installer normally handles this) |
