# HyperV Network Switcher

A Windows system-tray application that automatically connects your Hyper-V virtual machine to the correct virtual network switch based on which physical network the host is connected to.

---

## What it does

When you move between networks — office LAN, home Wi-Fi, mobile hotspot — the virtual machine needs a different network connection:

| Host network | VM should use |
|---|---|
| Office LAN (`10.0.0.0/23`, adapter `48:65:EE:18:86:EF`) | **Bridged** switch (full LAN access) |
| Anything else | **Default Switch** (NAT, always works) |

The app watches for network changes in the background. The moment the host connects to a recognised network, the VM's NIC is silently reconnected to the right Hyper-V virtual switch. If no rule matches, it falls back to the Default Switch automatically.

---

## Requirements

- Windows 11 host with **Hyper-V** enabled
- The user account must be a member of the **Hyper-V Administrators** group (or run as Administrator)
- [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (or use the self-contained publish below)
- The two virtual switches **Bridged** and **Default Switch** must exist in Hyper-V Virtual Switch Manager

---

## Setup

1. Edit `config.json` (lives next to the `.exe`) to describe your networks and VMs — see [Configuration](#configuration) below.
2. Run or publish the application (see below).
3. A tray icon appears. Right-click it at any time to see status, override manually, or add new network rules.

---

## Running

### Development / debug run

```powershell
cd HyperVNetworkSwitcher
dotnet run
```

> The app requires elevation (UAC prompt) because it controls Hyper-V switches.

### Install (recommended)

Run the included install script from the project root:

```powershell
.\Install.ps1
```

What it does:
1. Stops any running instance of the app
2. Publishes a self-contained single-file executable (`dotnet publish`)
3. Copies the executable to `%LOCALAPPDATA%\Programs\HyperVNetworkSwitcher\`
4. Copies `config.json` to the same folder — **only if one does not already exist there** (your edited config is never overwritten)
5. Asks whether to launch the app immediately (UAC prompt will appear)

To install and launch in one step:

```powershell
.\Install.ps1 -Launch
```

> **No .NET installation required** on the target machine — the published exe is fully self-contained.

### Publish manually

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Output: `bin\Release\net10.0-windows\win-x64\publish\HyperVNetworkSwitcher.exe`

### Auto-start with Windows

Right-click the tray icon → **Run on startup** to toggle the registry entry under `HKCU\...\CurrentVersion\Run`.

---

## System tray menu

| Item | Description |
|---|---|
| **HOST NETWORK** *(section header)* | |
| Adapter | Name of the active host network adapter |
| IP | IPv4 address of the host on the current network |
| Gateway | Default gateway address |
| DNS | Up to two DNS server addresses, separated by `·` |
| **VIRTUAL MACHINE** *(section header)* | |
| VM | Name of the managed Hyper-V virtual machine |
| Switch | Currently connected Hyper-V virtual switch |
| Rule | Name of the matched config rule (or "Fallback") |
| IP | IPv4 address inside the VM (refreshes ~3 s after each switch change) |
| **Force Re-evaluate** | Re-runs rule matching and applies any change immediately |
| **Manual Override ▶** | Sub-menu to force a specific VM → switch combination |
| **Add current network as bridged** | Detects the current adapter, shows a confirmation dialog, and appends a new Bridged rule to `config.json` |
| **Open config.json** | Opens the config file in your default `.json` editor (falls back to Explorer if none is set) |
| **Reload config** | Hot-reloads `config.json` without restarting |
| **Run on startup** | Toggle auto-start at Windows login |
| **Exit** | Stops the application |

---

## Configuration

`config.json` is loaded from the same directory as the executable. It is watched for changes — edits take effect immediately without a restart.

```jsonc
{
  "virtualMachines": [
    {
      "name":          "vDev-2026",        // Hyper-V VM name (exact)
      "nicName":       "Network Adapter",  // NIC name inside Hyper-V manager
      "defaultSwitch": "Default Switch"    // Fallback switch for this VM
    }
  ],
  "rules": [
    {
      "name":          "Office LAN",       // Shown in the tray status
      "priority":      1,                  // Lower = evaluated first
      "conditions": {
        "adapterMac":  "48:65:EE:18:86:EF", // Host NIC MAC (optional)
        "ipCidr":      "10.0.0.0/23"         // Host IP must fall in this range (optional)
      },
      "virtualSwitch": "Bridged",          // Hyper-V switch to connect to
      "targetVms":     ["vDev-2026"]       // VMs to reconnect
    }
  ],
  "fallback": {
    "virtualSwitch": "Default Switch",     // Used when no rule matches
    "targetVms":     ["vDev-2026"]
  }
}
```

### Adding a new network rule

**Option A — from the tray:** Connect to the network, then right-click the tray icon → **Add current network as bridged**. The app reads the current adapter MAC and subnet automatically.

**Option B — manually:** Add an object to the `rules` array in `config.json`. Both `adapterMac` and `ipCidr` are optional; omitting both means the rule matches any active adapter.

---

## Logging

Logs are written to:
```
%APPDATA%\HyperVNetworkSwitcher\switcher.log
```

Each switch change, rule evaluation, and error is recorded there.

---

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| UAC prompt on every launch | Normal — required for Hyper-V access |
| Status shows "Fallback" on the office LAN | MAC or CIDR in the rule does not match — check `switcher.log` |
| VM IP shows "no IP" | VM is powered off, or DHCP hasn't responded yet — wait a few seconds and Force Re-evaluate |
| Switch change fails silently | User account lacks Hyper-V Administrator rights |
