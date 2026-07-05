using System.Diagnostics;
using Microsoft.UI.Xaml.Controls;
using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using HyperVManagerTray.Services;

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
    private readonly ToggleMenuFlyoutItem _startupItem  = new() { Text = "Run on startup" };

    private MenuFlyoutItem? _updateBadge;

    /// <summary>UI dispatcher — captured on the UI thread in the constructor.</summary>
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _ui;

    /// <summary>
    /// Last known state of the auto-start scheduled task, refreshed in the background on
    /// every <see cref="RefreshState"/> call.  Reads/writes are always a single bool so
    /// no lock is needed; <c>volatile</c> prevents stale reads from cache lines.
    /// </summary>
    private volatile bool _cachedStartupEnabled;

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

        _startupItem.Command = new RelayCommand(ToggleStartup);

        Flyout = new MenuFlyout();

        Flyout.Items.Add(_vmPowerMenu);
        Flyout.Items.Add(new MenuFlyoutSeparator());

        var vmNetworkMenu = new MenuFlyoutSubItem { Text = "VM Network" };
        vmNetworkMenu.Items.Add(new MenuFlyoutItem { Text = "Re-check network now", Command = new RelayCommand(() => _ = _monitor.ForceEvaluateAsync()) });
        vmNetworkMenu.Items.Add(new MenuFlyoutItem { Text = "Repair host networking (host offline, VM online)", Command = new RelayCommand(() => _ = RepairHostNetworkingAsync()) });
        vmNetworkMenu.Items.Add(new MenuFlyoutSeparator());
        vmNetworkMenu.Items.Add(_overrideMenu);
        vmNetworkMenu.Items.Add(new MenuFlyoutSeparator());
        vmNetworkMenu.Items.Add(new MenuFlyoutItem { Text = "Add current network as a bridged rule", Command = new RelayCommand(AddCurrentAsBridged) });
        Flyout.Items.Add(vmNetworkMenu);
        Flyout.Items.Add(new MenuFlyoutSeparator());

        var settingsMenu = new MenuFlyoutSubItem { Text = "Settings" };
        settingsMenu.Items.Add(new MenuFlyoutItem { Text = "Open config.json", Command = new RelayCommand(() => OpenPath(ConfigManager.GetConfigPath())) });
        settingsMenu.Items.Add(new MenuFlyoutItem { Text = "Open log file",    Command = new RelayCommand(() => OpenPath(AppInfo.LogFile)) });
        settingsMenu.Items.Add(new MenuFlyoutSeparator());
        settingsMenu.Items.Add(new MenuFlyoutItem { Text = "Reload config from disk", Command = new RelayCommand(() => _ = Task.Run(() => _config.Load())) });
        settingsMenu.Items.Add(new MenuFlyoutSeparator());
        settingsMenu.Items.Add(_startupItem);
        settingsMenu.Items.Add(new MenuFlyoutSeparator());
        settingsMenu.Items.Add(new MenuFlyoutItem { Text = "Check for updates", Command = new RelayCommand(() => _ = CheckForUpdatesAsync()) });
        Flyout.Items.Add(settingsMenu);
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

        // Use the cached value immediately — never block the UI thread for schtasks.exe.
        // Fire a background task to refresh the cache; the check mark is updated on the
        // UI thread when the query returns (typically within 500 ms).
        _startupItem.IsChecked = _cachedStartupEnabled;
        _ = Task.Run(() =>
        {
            bool enabled;
            try   { enabled = _startup.IsEnabled; }
            catch { enabled = false; }
            _cachedStartupEnabled = enabled;
            _ui.TryEnqueue(() => _startupItem.IsChecked = enabled);
        });
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
        => new() { Text = text, Command = new RelayCommand(() => _ = action()) };

    /// <summary>A menu item that fires a VM power action — synchronous, non-blocking (see <see cref="VmService.BeginPowerAction"/>).</summary>
    private MenuFlyoutItem PowerItem(string text, string vmName, VmOpKind kind)
        => new() { Text = text, Command = new RelayCommand(() => _vm.BeginPowerAction(vmName, kind)) };

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

    private async Task StartAndConnect(string vmName, string nicName)
    {
        // BeginPowerAction is fire-and-forget; the flat delay is the same heuristic the old
        // PowerShell path used to give the VM time to boot before vmconnect can attach.
        _vm.BeginPowerAction(vmName, VmOpKind.Start);
        await Task.Delay(2500);
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
                    Command = new RelayCommand(() => _ = _monitor.ManualOverrideAsync(vmName, swName)),
                });
            }
        }
    }

    // ── Actions ─────────────────────────────────────────────────────────────────

    private void AddCurrentAsBridged()
    {
        var info = AdapterMatcher.GetCurrentNetworkInfo();
        if (info is null)
        {
            NativeMethods.Warn("No active network adapter with an IPv4 address was found.", AppName);
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

        if (!NativeMethods.Confirm(
                $"Add the following rule to config.json?\n\n" +
                $"  Adapter :  {info.AdapterDescription}\n" +
                $"  MAC     :  {info.Mac}\n" +
                $"  Network :  {info.IpCidr}\n" +
                $"  Switch  :  {bridgedSwitch}",
                "Add Current Network as Bridged"))
            return;

        var rule = new NetworkRule
        {
            Name          = info.AdapterDescription.Length > 40 ? info.AdapterDescription[..40].TrimEnd() : info.AdapterDescription,
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

    private Task CheckForUpdatesAsync()
    {
        // Capture the foreground HWND now (tray flyout is open) so the update dialog has a parent
        // even if the flyout is dismissed by the time the HTTP check completes. The shared flow
        // must stay on the UI thread (comctl32 v6 activation context for TaskDialogIndirect).
        var hwnd = NativeMethods.CaptureHwnd();
        return UpdatePrompt.RunAsync(_updateChecker, hwnd);
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
            Command = new RelayCommand(() => Shell.Open(result.ReleasePageUrl)),
        };

        // Badge + separator always sit above everything else in the menu.
        Flyout.Items.Insert(0, _updateBadge);
        Flyout.Items.Insert(1, new MenuFlyoutSeparator());
    }

    private void ToggleStartup()
    {
        // schtasks.exe Create/Delete/Query can take 500 ms – 2 s.  Run everything on the
        // thread pool so the UI thread stays responsive.  NativeMethods.Warn/Info use
        // MessageBoxW which is safe to call from any thread.
        _ = Task.Run(() =>
        {
            try
            {
                if (_startup.IsEnabled) _startup.Disable();
                else _startup.Enable(Environment.ProcessPath
                    ?? throw new InvalidOperationException("Cannot determine executable path."));
            }
            catch (Exception ex)
            {
                _ui.TryEnqueue(() =>
                    NativeMethods.Warn($"Could not change the startup setting:\n\n{ex.Message}", AppName));
                return;
            }

            // Refresh cache + check mark after the toggle completes.
            bool enabled;
            try   { enabled = _startup.IsEnabled; }
            catch { enabled = false; }
            _cachedStartupEnabled = enabled;
            _ui.TryEnqueue(() => _startupItem.IsChecked = enabled);
        });
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private const string AppName = AppInfo.Name;

    private void ShowAbout()
    {
        _ui.TryEnqueue(() =>
        {
            var about = new AboutWindow(_updateChecker);
            about.Activate();
        });
    }

    private void Add(string text, Action action)
        => Flyout.Items.Add(new MenuFlyoutItem { Text = text, Command = new RelayCommand(action) });

    // Open a file in its default handler; if that fails (e.g. no association), fall back to
    // revealing it in Explorer.
    private static void OpenPath(string path)
    {
        if (Shell.Open(path)) return;
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { /* ignore */ }
    }
}
