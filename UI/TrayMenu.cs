using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using HyperVManagerTray.Services;
using ZeroZero.Brand.Core;
using ZeroZero.Brand.WinUI;

namespace HyperVManagerTray.UI;

/// <summary>
/// The tray icon's right-click context menu.
///
/// H.NotifyIcon builds a native Win32 popup menu from <see cref="Flyout"/> on every right-click
/// and invokes each item's <c>Command</c> (the XAML <c>Click</c>/<c>Opening</c> events do NOT
/// fire for the native menu).  Items are created once with command bindings; <see cref="RefreshState"/>
/// resyncs the dynamic parts (override list, startup check) right before the menu opens.
/// </summary>
internal sealed class TrayMenu
{
    private readonly ConfigManager  _config;
    private readonly NetworkMonitor _monitor;
    private readonly HyperVManager  _hyperV;   // switch binding / host-vNIC repair (PowerShell, Phase 1)
    private readonly VmService      _vm;       // VM status/power/IPs (WMI)
    private readonly StartupManager _startup;
    private readonly UpdateChecker  _updateChecker;

    private readonly MenuFlyoutSubItem    _overrideMenu = new() { Text = "Override VM switch" };
    private readonly MenuFlyoutSubItem    _vmPowerMenu  = new() { Text = "VM Power" };

    private MenuFlyoutItem? _updateBadge;
    private BrandAboutWindow? _aboutWindow;
    private SettingsWindow?   _settingsWindow;

    /// <summary>UI dispatcher — captured on the UI thread in the constructor.</summary>
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _ui;

    public MenuFlyout Flyout { get; }

    public TrayMenu(ConfigManager config, NetworkMonitor monitor, HyperVManager hyperV, VmService vm,
                    StartupManager startup, UpdateChecker updateChecker, Action onExit)
    {
        _config        = config;
        _monitor       = monitor;
        _hyperV        = hyperV;
        _vm            = vm;
        _startup       = startup;
        _updateChecker = updateChecker;

        _ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("TrayMenu must be created on the UI thread.");

        Flyout = new MenuFlyout();

        // Tray menu holds QUICK ACTIONS only (issue #18). VM power + live network actions stay here;
        // everything configuration-shaped (adapter rename, run-on-startup, log level, per-VM
        // on-bridge-lost action, open config/log, reload, check-for-updates) moved into the
        // Settings window. "Add current network as a bridged rule" stays: it captures the LIVE
        // current network in one tap, which only makes sense from the tray.
        Flyout.Items.Add(_vmPowerMenu);
        Flyout.Items.Add(new MenuFlyoutSeparator());

        var vmNetworkMenu = new MenuFlyoutSubItem { Text = "VM Network" };
        vmNetworkMenu.Items.Add(new MenuFlyoutItem { Text = "Re-check network now", Command = new RelayCommand(() => { LogClick("Re-check network now"); _ = _monitor.ForceEvaluateAsync(); }) });
        vmNetworkMenu.Items.Add(new MenuFlyoutItem { Text = "Repair host networking (host offline, VM online)", Command = new RelayCommand(() => { LogClick("Repair host networking"); _ = RepairHostNetworkingAsync(); }) });
        vmNetworkMenu.Items.Add(new MenuFlyoutSeparator());
        vmNetworkMenu.Items.Add(_overrideMenu);
        vmNetworkMenu.Items.Add(new MenuFlyoutSeparator());
        vmNetworkMenu.Items.Add(new MenuFlyoutItem { Text = "Add current network as a bridged rule", Command = new RelayCommand(() => { LogClick("Add current network as a bridged rule"); _ = AddCurrentAsBridged(); }) });
        Flyout.Items.Add(vmNetworkMenu);
        Flyout.Items.Add(new MenuFlyoutSeparator());

        Add("Settings…", ShowSettings);
        Flyout.Items.Add(new MenuFlyoutSeparator());
        Add("About…", ShowAbout);
        Flyout.Items.Add(new MenuFlyoutSeparator());
        Add("Exit", onExit);

        RefreshState();
    }

    /// <summary>Re-reads live state into the dynamic menu parts. Call right before the menu opens.</summary>
    public void RefreshState()
    {
        RebuildOverrideMenu();
        RebuildVmPowerMenu();
    }

    private void RebuildVmPowerMenu()
    {
        _vmPowerMenu.Items.Clear();

        // ── Read from in-memory cache ONLY — never block the UI thread ────────────
        // GetCachedVmsSync() returns null until the first background discovery completes.
        // When null, show a placeholder and the cache pre-warm (started by App at launch)
        // will call RefreshState() once data arrives.
        var allVms = _vm.GetCachedVmsSync();

        if (allVms is null)
        {
            // Cache not yet populated (first few seconds after startup).
            // Show managed-VM submenus from config (power ops only — no unmanaged section yet).
            foreach (var vm in _config.Current.VirtualMachines)
            {
                var sub = new MenuFlyoutSubItem { Text = vm.Name };
                AddPowerItems(sub, vm.Name, vm.NicName);
                _vmPowerMenu.Items.Add(sub);
            }

            if (_vmPowerMenu.Items.Count == 0)
                _vmPowerMenu.Items.Add(new MenuFlyoutItem
                    { Text = "Loading VMs…", IsEnabled = false });

            // No background refresh needed here — App.PreWarmVmCacheAsync() owns that.
            return;
        }

        // ── Cache is populated — build the full menu ───────────────────────────
        var configNames = new HashSet<string>(_config.Current.VirtualMachines
            .Select(v => v.Name), StringComparer.OrdinalIgnoreCase);

        // Managed VMs (in config) — full power submenu + Remove from config
        foreach (var vm in _config.Current.VirtualMachines)
        {
            var name = vm.Name;
            var sub  = new MenuFlyoutSubItem { Text = vm.Name };
            AddPowerItems(sub, name, vm.NicName);
            sub.Items.Add(new MenuFlyoutSeparator());
            sub.Items.Add(Item("Remove from config…", async () =>
            {
                if (!NativeMethods.Confirm(
                        $"Remove {name} from config.json?\n\nThis only removes the app's management of this VM — it does not delete the VM.",
                        "Remove VM from Config"))
                    return;

                try
                {
                    await Task.Run(() => _config.RemoveVmFromConfig(name)).ConfigureAwait(false);
                    NativeMethods.Info($"{name} removed from config.", AppName);
                }
                catch (Exception ex)
                {
                    NativeMethods.Error($"Failed to remove VM from config:\n{ex.Message}", AppName);
                }
            }));
            _vmPowerMenu.Items.Add(sub);
        }

        // Unmanaged VMs (discovered but not in config) — limited submenu
        var unmanaged = allVms
            .Where(d => !configNames.Contains(d.Name))
            .OrderBy(d => d.Name)
            .ToList();

        if (unmanaged.Count > 0 && _vmPowerMenu.Items.Count > 0)
            _vmPowerMenu.Items.Add(new MenuFlyoutSeparator());

        foreach (var vm in unmanaged)
        {
            var name    = vm.Name;
            var nicName = vm.NicName;
            var sub     = new MenuFlyoutSubItem { Text = $"{name} (unmanaged)" };
            sub.Items.Add(PowerItem("Start",    name, VmOpKind.Start));
            sub.Items.Add(PowerItem("Shutdown", name, VmOpKind.Shutdown));
            sub.Items.Add(Item("Connect",  () => { Shell.OpenVmConnect(name); return Task.CompletedTask; }));
            sub.Items.Add(new MenuFlyoutSeparator());
            sub.Items.Add(Item("Add to config…", async () =>
            {
                try
                {
                    await Task.Run(() => _config.AddVmToConfig(name, nicName)).ConfigureAwait(false);
                    NativeMethods.Info(
                        $"Added \"{name}\" to config.\nReload to manage it fully.",
                        AppName);
                }
                catch (Exception ex)
                {
                    NativeMethods.Error($"Failed to add VM to config:\n{ex.Message}", AppName);
                }
            }));
            _vmPowerMenu.Items.Add(sub);
        }

        if (_vmPowerMenu.Items.Count == 0)
            _vmPowerMenu.Items.Add(new MenuFlyoutItem { Text = "(no VMs found)", IsEnabled = false });

        // Kick off a background cache refresh so the *next* menu open is up-to-date.
        _ = Task.Run(async () =>
        {
            try { await _vm.RefreshOnceAsync().ConfigureAwait(false); }
            catch { /* non-fatal */ }
        });
    }

    private MenuFlyoutItem Item(string text, Func<Task> action)
        => new() { Text = text, Command = new RelayCommand(() => { LogClick(text); _ = action(); }) };

    /// <summary>A menu item that fires a VM power action — synchronous, non-blocking (see <see cref="VmService.BeginPowerAction"/>).</summary>
    private MenuFlyoutItem PowerItem(string text, string vmName, VmOpKind kind)
        => new() { Text = text, Command = new RelayCommand(() =>
           {
               UiActivityLog.Logger.LogInformation("Tray: {Command} '{Vm}'", text, vmName);
               _vm.BeginPowerAction(vmName, kind, VmOpOrigin.Tray);
           }) };

    /// <summary>The full power-action set for a managed VM: Start/Start&&Connect/Shutdown/Pause/Resume/Save.</summary>
    private void AddPowerItems(MenuFlyoutSubItem sub, string vmName, string nicName)
    {
        sub.Items.Add(PowerItem("Start",       vmName, VmOpKind.Start));
        sub.Items.Add(Item("Start && Connect", () => StartAndConnect(vmName, nicName)));
        sub.Items.Add(PowerItem("Shutdown",    vmName, VmOpKind.Shutdown));
        sub.Items.Add(PowerItem("Pause",       vmName, VmOpKind.Pause));
        sub.Items.Add(PowerItem("Resume",      vmName, VmOpKind.Resume));
        sub.Items.Add(PowerItem("Save",        vmName, VmOpKind.Save));
    }

    // Cold boot under host load can legitimately take a while; long enough to cover that without
    // hanging the tray menu's "Start && Connect" item indefinitely if the VM is unusually slow to
    // report in. Same value as DashboardWindow.StartAndConnectTimeout — kept local since VmService is
    // the actual shared logic (see WaitUntilRunningAsync) and a shared constant here would be
    // over-engineering for a single TimeSpan literal.
    private static readonly TimeSpan StartAndConnectTimeout = TimeSpan.FromSeconds(45);

    private async Task StartAndConnect(string vmName, string nicName)
    {
        // BeginPowerAction is fire-and-forget; WaitUntilRunningAsync replaces the old flat 2.5s guess
        // with an actual readiness wait (event-driven off VmService.StatusesChanged — see its doc
        // comment). On timeout it proceeds anyway rather than silently doing nothing.
        _vm.BeginPowerAction(vmName, VmOpKind.Start, VmOpOrigin.Tray);
        await _vm.WaitUntilRunningAsync(vmName, StartAndConnectTimeout);
        var sw = _monitor.LastApplied?.VirtualSwitch;
        if (!string.IsNullOrEmpty(sw)) await _hyperV.ApplySwitchAsync(vmName, nicName, sw);
    }

    // ── Dynamic submenus ────────────────────────────────────────────────────────

    private void RebuildOverrideMenu()
    {
        _overrideMenu.Items.Clear();

        var switches = new HashSet<string> { _config.Current.Fallback.VirtualSwitch };
        foreach (var rule in _config.Current.Rules) switches.Add(rule.VirtualSwitch);
        var orderedSwitches = switches.Order().ToList();

        foreach (var vm in _config.Current.VirtualMachines)
        {
            foreach (var sw in orderedSwitches)
            {
                var vmName = vm.Name;
                var swName = sw;
                _overrideMenu.Items.Add(new MenuFlyoutItem
                {
                    Text    = $"{vm.Name} → {sw}",
                    Command = new RelayCommand(() =>
                    {
                        UiActivityLog.Logger.LogInformation("Tray: Override switch '{Vm}' → '{Switch}'", vmName, swName);
                        _ = _monitor.ManualOverrideAsync(vmName, swName);
                    }),
                });
            }
        }
    }

    // ── Actions ─────────────────────────────────────────────────────────────────

    private async Task AddCurrentAsBridged()
    {
        // GetCurrentNetworkInfo enumerates all NICs (GetAllNetworkInterfaces + GetIPProperties) and can
        // block for hundreds of ms; this runs from a tray command on the UI thread, so offload it to the
        // thread pool to keep the UI responsive (issue #29, finding 3).
        var info = await Task.Run(AdapterMatcher.GetCurrentNetworkInfo);
        if (info is null)
        {
            NativeMethods.Warn("No active network adapter with an IPv4 address was found.", AppName);
            return;
        }

        // A Wi-Fi adapter surfaces as Msvm_WiFiPort, which the switch-binding path never targets, so a
        // rule bound to it could never take effect (issue #29, finding 5). Reject it up front with an
        // explanation rather than silently saving a rule that will never bridge.
        if (info.IsWireless)
        {
            NativeMethods.Warn(
                $"\"{info.AdapterDescription}\" is a Wi-Fi adapter.\n\n" +
                "Bridging a Hyper-V switch onto a wireless adapter isn't supported — the switch can only " +
                "bind to a wired (Ethernet) adapter, such as a USB-Ethernet dock. No rule was added.",
                AppName);
            return;
        }

        var normNew   = AdapterMatcher.NormalizeMac(info.Mac);
        var duplicate = _config.Current.Rules.FirstOrDefault(r =>
            r.Conditions.AdapterMac is not null &&
            AdapterMatcher.NormalizeMac(r.Conditions.AdapterMac) == normNew);
        if (duplicate is not null)
        {
            NativeMethods.Info($"This adapter is already covered by rule \"{duplicate.Name}\".\n\nEdit config.json to update it.", AppName);
            return;
        }

        var fallbackSwitch = _config.Current.Fallback.VirtualSwitch;
        var bridgedSwitch  = _config.Current.Rules
            .Select(r => r.VirtualSwitch)
            .Where(s => s != fallbackSwitch)
            .OrderBy(s => s.Contains("bridge", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault() ?? "Bridged";

        // Ask for a memorable name ("Home", "Office", "Coffee shop …") instead of silently using
        // the raw adapter description (e.g. "Intel(R) Wi-Fi 6 AX201 160MHz") as the rule name —
        // that says nothing about WHERE the network is. Pre-filled with the adapter description as
        // a convenient starting point; Cancel here aborts the whole "add rule" flow.
        var defaultName = info.AdapterDescription.Length > 40 ? info.AdapterDescription[..40].TrimEnd() : info.AdapterDescription;
        var name = await TextPromptWindow.ShowAsync(
            "Add Current Network as Bridged",
            $"Name this network (adapter: {info.AdapterDescription}):",
            defaultName);
        if (name is null) return;

        if (!NativeMethods.Confirm(
                $"Add the following rule to config.json?\n\n" +
                $"  Name    :  {name}\n" +
                $"  Adapter :  {info.AdapterDescription}\n" +
                $"  MAC     :  {info.Mac}\n" +
                $"  Network :  {info.IpCidr}\n" +
                $"  Switch  :  {bridgedSwitch}",
                "Add Current Network as Bridged"))
            return;

        var rule = new NetworkRule
        {
            Name          = name,
            Priority      = _config.Current.Rules.Count > 0 ? _config.Current.Rules.Max(r => r.Priority) + 10 : 10,
            Conditions    = new RuleConditions { AdapterMac = info.Mac, IpCidr = info.IpCidr },
            VirtualSwitch = bridgedSwitch,
            TargetVms     = _config.Current.Fallback.TargetVms.ToList(),
        };

        _ = Task.Run(() =>
        {
            try
            {
                _config.AddBridgedRule(rule);
                _ = _monitor.ForceEvaluateAsync();
            }
            catch (Exception ex)
            {
                NativeMethods.Error($"Failed to save rule:\n{ex.Message}", AppName);
            }
        });
    }

    /// <summary>
    /// Manual escape hatch for the "host offline but VM online" failure: collapses any duplicate
    /// host vNIC on each configured bridged switch back to one (see
    /// <see cref="HyperVManager.RepairHostVNicAsync"/>) and reports the outcome.
    /// </summary>
    private async Task RepairHostNetworkingAsync()
    {
        try
        {
            var switches = _config.Current.RuleSwitches.ToList();

            if (switches.Count == 0)
            {
                NativeMethods.Info("No bridged switches are configured — nothing to repair.", AppName);
                return;
            }

            var repaired = new List<string>();
            bool anyError = false;
            foreach (var sw in switches)
            {
                var state = await _hyperV.RepairHostVNicAsync(sw).ConfigureAwait(false);
                if (state is HyperVManager.HostVNicState.Repaired or HyperVManager.HostVNicState.Reshared)
                    repaired.Add(sw);
                else if (state == HyperVManager.HostVNicState.Error)
                    anyError = true;
            }

            if (repaired.Count > 0)
                NativeMethods.Info(
                    $"Repaired host networking on: {string.Join(", ", repaired)}.\n\n" +
                    "A duplicate host network adapter was collapsed back to one — your wired connection should return within a few seconds.",
                    AppName);
            else if (anyError)
                NativeMethods.Warn("Could not repair host networking. See the log file for details.", AppName);
            else
                NativeMethods.Info("Host networking looks healthy — nothing to repair.", AppName);
        }
        catch (Exception ex)
        {
            NativeMethods.Warn($"Repair failed:\n{ex.Message}", AppName);
        }
    }

    private async Task<bool> CheckForUpdatesAsync()
    {
        // Capture the foreground HWND now (tray flyout is open) so the update dialog has a parent
        // even if the flyout is dismissed by the time the HTTP check completes. The shared flow
        // must stay on the UI thread (comctl32 v6 activation context for TaskDialogIndirect).
        var hwnd = NativeMethods.CaptureHwnd();
        await UpdatePrompt.RunAsync(_updateChecker, hwnd);
        // The installer (Inno Setup, CloseApplications=yes) closes and relaunches the app itself, so
        // the shared About window never needs to own the exit — always report "no self-exit required".
        return false;
    }

    /// <summary>
    /// Inserts (or updates) an "Update available" badge at the top of the tray menu.
    /// Safe to call more than once — subsequent calls only refresh the version text.
    /// Clicking the badge opens the GitHub releases page immediately; the user can
    /// get the full release-notes dialog via Settings → Check for updates.
    /// </summary>
    public void SetUpdateBadge(UpdateChecker.CheckResult result)
    {
        if (_updateBadge is not null)
        {
            _updateBadge.Text = $"⬆  Update available: v{result.LatestVersion}";
            return;
        }

        _updateBadge = new MenuFlyoutItem
        {
            Text    = $"⬆  Update available: v{result.LatestVersion}",
            Command = new RelayCommand(() => { LogClick("Update badge → releases page"); Shell.Open(result.ReleasePageUrl); }),
        };

        // Badge + separator always sit above everything else in the menu.
        Flyout.Items.Insert(0, _updateBadge);
        Flyout.Items.Insert(1, new MenuFlyoutSeparator());
    }

    /// <summary>
    /// Opens the consolidated Settings window (issue #18) — a reused singleton, mirroring the About
    /// window, so repeated clicks re-activate the one window instead of stacking duplicates.
    /// </summary>
    private void ShowSettings()
    {
        _ui.TryEnqueue(() =>
        {
            if (_settingsWindow is not null)
            {
                UiActivityLog.Logger.LogInformation("Window: Settings re-activated");
                _settingsWindow.Activate();
                return;
            }

            UiActivityLog.Logger.LogInformation("Window: Settings opened");
            _settingsWindow = new SettingsWindow(_config, _startup, _updateChecker);
            _settingsWindow.Closed += (_, _) =>
            {
                UiActivityLog.Logger.LogInformation("Window: Settings closed");
                _settingsWindow = null;
            };
            _settingsWindow.Activate();
        });
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private const string AppName = AppInfo.Name;

    private void ShowAbout()
    {
        _ui.TryEnqueue(() =>
        {
            // Reuse the one open About window rather than stacking duplicates on repeated clicks.
            if (_aboutWindow is not null)
            {
                UiActivityLog.Logger.LogInformation("Window: About re-activated");
                _aboutWindow.Activate();
                return;
            }

            UiActivityLog.Logger.LogInformation("Window: About opened");

            var options = new BrandAboutOptions
            {
                Info = new AboutInfo
                {
                    AppName     = AppName,
                    Version     = AppInfo.Version,
                    Description = "Automatically connects Hyper-V VMs to the right virtual switch when the host changes networks. Manage VM power and state directly from the system tray.",
                    RepoUrl     = "https://github.com/0z00z0/HyperVManagerTray",
                    // Every third-party runtime package the app references (see the csproj + README
                    // "External libraries" table — kept in sync with both). H.NotifyIcon.WinUI is the
                    // only non-Microsoft dependency; the Microsoft packages ship under the Microsoft
                    // Software Licence Terms (the WinAppSDK *source* is MIT on GitHub).
                    ExternalLibraries =
                    [
                        new ExternalLibrary("Microsoft.WindowsAppSDK", "Microsoft", "WinUI 3 framework (windowing, XAML, Mica)", "MS-EULA", "https://github.com/microsoft/WindowsAppSDK"),
                        new ExternalLibrary("Microsoft.Windows.SDK.BuildTools", "Microsoft", "Windows SDK build tooling for the App SDK", "MS-EULA", "https://www.nuget.org/packages/Microsoft.Windows.SDK.BuildTools"),
                        new ExternalLibrary("H.NotifyIcon.WinUI", "HavenDV", "System-tray icon + native context menu for WinUI 3", "MIT", "https://github.com/HavenDV/H.NotifyIcon"),
                        new ExternalLibrary("System.Drawing.Common", "Microsoft", "Renders the tray .ico at runtime", "MIT", "https://www.nuget.org/packages/System.Drawing.Common"),
                        new ExternalLibrary("Microsoft.Extensions.Logging", "Microsoft", "Logging abstraction; output goes to a small custom file sink", "MIT", "https://www.nuget.org/packages/Microsoft.Extensions.Logging"),
                        new ExternalLibrary("System.Management", "Microsoft", "WMI access (root\\virtualization\\v2) for VM status/power", "MIT", "https://www.nuget.org/packages/System.Management"),
                    ],
                },
                // The shared window's "Check for Updates" reuses this class's own flow (which wraps
                // UpdatePrompt.RunAsync via NativeMethods.CaptureHwnd()); it returns false because the
                // Inno installer restarts the app itself, so no self-exit is needed. No update
                // machinery moved into the shared library.
                OnCheckForUpdates = CheckForUpdatesAsync,
            };

            _aboutWindow = new BrandAboutWindow(options);
            _aboutWindow.Closed += (_, _) =>
            {
                UiActivityLog.Logger.LogInformation("Window: About closed");
                _aboutWindow = null;
            };
            _aboutWindow.Activate();
        });
    }

    private void Add(string text, Action action)
        => Flyout.Items.Add(new MenuFlyoutItem { Text = text, Command = new RelayCommand(() => { LogClick(text); action(); }) });

    /// <summary>Records a tray-menu command invocation to ui.log (issue #21). Menu text only — no PII.</summary>
    private static void LogClick(string command) =>
        UiActivityLog.Logger.LogInformation("Tray: {Command}", command);
}
