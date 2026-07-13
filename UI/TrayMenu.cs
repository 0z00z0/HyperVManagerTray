using System.Diagnostics;
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
    private readonly MenuFlyoutSubItem    _renameAdapterMenu = new() { Text = "Rename network adapter" };
    private readonly ToggleMenuFlyoutItem _startupItem  = new() { Text = "Run on startup" };

    private MenuFlyoutItem? _updateBadge;
    private BrandAboutWindow? _aboutWindow;

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
        vmNetworkMenu.Items.Add(new MenuFlyoutItem { Text = "Add current network as a bridged rule", Command = new RelayCommand(() => _ = AddCurrentAsBridged()) });
        Flyout.Items.Add(vmNetworkMenu);
        Flyout.Items.Add(new MenuFlyoutSeparator());

        // Rename a physical NIC's displayed description (issue #15). Submenu is rebuilt on every
        // open (RefreshState) so the adapter list is current.
        Flyout.Items.Add(_renameAdapterMenu);
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
        RebuildRenameAdapterMenu();

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
        _vm.BeginPowerAction(vmName, VmOpKind.Start);
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
                    Command = new RelayCommand(() => _ = _monitor.ManualOverrideAsync(vmName, swName)),
                });
            }
        }
    }

    /// <summary>
    /// Rebuilds the "Rename network adapter" submenu — one entry per physical NIC (alias + current
    /// description), each opening the rename dialog for that adapter.  Populated from
    /// <see cref="AdapterMatcher.GetPhysicalAdapters"/> so it reuses the rule engine's filtering.
    /// </summary>
    private void RebuildRenameAdapterMenu()
    {
        _renameAdapterMenu.Items.Clear();

        IReadOnlyList<PhysicalAdapterInfo> adapters;
        try   { adapters = AdapterMatcher.GetPhysicalAdapters(); }
        catch { adapters = []; }

        if (adapters.Count == 0)
        {
            _renameAdapterMenu.Items.Add(new MenuFlyoutItem { Text = "(no adapters found)", IsEnabled = false });
            return;
        }

        foreach (var a in adapters)
        {
            var adapter = a;   // capture per iteration
            var label = string.IsNullOrWhiteSpace(a.InterfaceAlias) || a.InterfaceAlias == a.Description
                ? a.Description
                : $"{a.InterfaceAlias} — {a.Description}";

            _renameAdapterMenu.Items.Add(new MenuFlyoutItem
            {
                Text    = label,
                Command = new RelayCommand(() => _ = RenameAdapterAsync(adapter)),
            });
        }
    }

    // ── Actions ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Rename flow for one adapter (issue #15): resolve its PnP device deterministically (abort on a
    /// 0/&gt;1 match), show the rename/reset dialog, then — only on explicit consent — write the new
    /// FriendlyName and offer a device restart.  The saved original is captured before the first write
    /// so Reset can restore it.
    /// </summary>
    private async Task RenameAdapterAsync(PhysicalAdapterInfo adapter)
    {
        AdapterNameRules.DeviceResolution resolution;
        try   { resolution = await Task.Run(() => AdapterRenamer.ResolveDevice(adapter.InterfaceGuid)); }
        catch (Exception ex)
        {
            NativeMethods.Error($"Could not identify the device for this adapter:\n{ex.Message}", AppName);
            return;
        }

        if (!resolution.Success || resolution.DeviceInstanceId is null)
        {
            // 0 or >1 devices resolved — never guess which dock; abort with no changes.
            NativeMethods.Error(
                $"Could not safely identify the device behind \"{adapter.Description}\".\n\n" +
                $"{resolution.Error}\n\nNo changes were made.",
                AppName);
            return;
        }

        var deviceInstanceId = resolution.DeviceInstanceId;

        var existing = _config.Current.AdapterNames.FirstOrDefault(o =>
            o.DeviceInstanceId.Equals(deviceInstanceId, StringComparison.OrdinalIgnoreCase));

        // Reset is offered only when we have a real original to restore (never delete — §5.4).
        bool canReset = existing is not null
                        && !existing.OriginalWasAbsent
                        && !string.IsNullOrEmpty(existing.OriginalFriendlyName);
        string? savedOriginal = canReset ? existing!.OriginalFriendlyName : null;

        var others = AdapterMatcher.GetPhysicalAdapters()
            .Where(p => !p.InterfaceGuid.Equals(adapter.InterfaceGuid, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Description)
            .ToList();

        var result = await RenameAdapterWindow.ShowAsync(adapter.Description, others, savedOriginal, canReset);
        if (result is null) return;

        if (result.Choice == RenameDialogChoice.Reset)
            await ResetAdapterNameAsync(adapter, deviceInstanceId, existing!);
        else
            await ApplyRenameAsync(adapter, deviceInstanceId, result.NewName!, existing);
    }

    private async Task ApplyRenameAsync(
        PhysicalAdapterInfo adapter, string deviceInstanceId, string newName, AdapterNameOverride? existing)
    {
        if (!NativeMethods.Confirm(
                "Rename this network adapter's description?\n\n" +
                $"  From :  {adapter.Description}\n" +
                $"  To   :  {newName}\n\n" +
                "This changes the description everywhere Windows shows it. To appear everywhere the " +
                "adapter may need to be disabled/enabled or the PC restarted, which will briefly drop " +
                "this adapter's network connection.",
                AppName))
            return;

        try
        {
            // Persist the true original BEFORE the first write so Reset can always restore it (§5.4).
            if (existing is null)
            {
                var (present, original) = await Task.Run(() => AdapterRenamer.ReadFriendlyName(deviceInstanceId));
                var entry = new AdapterNameOverride
                {
                    DeviceInstanceId     = deviceInstanceId,
                    OriginalFriendlyName = present ? (original ?? string.Empty) : string.Empty,
                    OriginalWasAbsent    = !present,
                    Mac                  = adapter.Mac,
                    RenamedOn            = DateTime.Now.ToString("yyyy-MM-dd"),
                    CurrentFriendlyName  = newName,
                };
                await Task.Run(() => _config.UpsertAdapterName(entry));
            }
            else
            {
                existing.CurrentFriendlyName = newName;   // keep the original; update last-applied only
                await Task.Run(() => _config.UpsertAdapterName(existing));
            }

            // ★ DEVICE-MUTATING WRITE ★ (SetupAPI, parameterized — no shell). Consent captured above.
            await Task.Run(() => AdapterRenamer.WriteFriendlyName(deviceInstanceId, newName));
        }
        catch (Exception ex)
        {
            NativeMethods.Error($"The rename could not be completed:\n{ex.Message}", AppName);
            return;
        }

        OfferDeviceRestart(deviceInstanceId, newName);
    }

    private async Task ResetAdapterNameAsync(
        PhysicalAdapterInfo adapter, string deviceInstanceId, AdapterNameOverride existing)
    {
        if (existing.OriginalWasAbsent || string.IsNullOrEmpty(existing.OriginalFriendlyName))
        {
            // Defensive: Reset should be disabled in this case. Never delete — it could leave two
            // identically-named adapters (§5.4).
            NativeMethods.Info(
                "This adapter had no custom description originally, so there is nothing to restore. " +
                "The app won't delete the name automatically.",
                AppName);
            return;
        }

        if (!NativeMethods.Confirm(
                "Restore this adapter's original description?\n\n" +
                $"  Current  :  {adapter.Description}\n" +
                $"  Restore  :  {existing.OriginalFriendlyName}\n\n" +
                "The adapter may need a restart or reboot to update everywhere, briefly dropping its connection.",
                AppName))
            return;

        try
        {
            existing.CurrentFriendlyName = existing.OriginalFriendlyName;
            await Task.Run(() => _config.UpsertAdapterName(existing));

            // ★ DEVICE-MUTATING WRITE ★ Restore the saved value (never a delete).
            await Task.Run(() => AdapterRenamer.WriteFriendlyName(deviceInstanceId, existing.OriginalFriendlyName));
        }
        catch (Exception ex)
        {
            NativeMethods.Error($"Could not restore the original name:\n{ex.Message}", AppName);
            return;
        }

        OfferDeviceRestart(deviceInstanceId, existing.OriginalFriendlyName);
    }

    /// <summary>
    /// After a successful FriendlyName write, offers to disable/enable the adapter so the change
    /// propagates immediately (warning about the brief link drop), or defer it to a later reboot.
    /// </summary>
    private void OfferDeviceRestart(string deviceInstanceId, string appliedName)
    {
        bool restart = NativeMethods.Confirm(
            $"The adapter description was set to \"{appliedName}\".\n\n" +
            "Some places may still show the old name until the adapter is restarted. " +
            "Restart (disable + enable) this adapter now?\n\n" +
            "Your network connection on this adapter will drop for a few seconds. " +
            "Choose No to apply it later — a PC restart will also pick it up.",
            AppName);

        if (!restart)
        {
            NativeMethods.Info(
                "The new name is saved. Restart the adapter or reboot to see it everywhere.", AppName);
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                // ★ DEVICE-MUTATING ★ Disable + enable to force NDIS to re-read the description.
                AdapterRenamer.RestartDevice(deviceInstanceId);

                // Confirm the name is still on disk after the cycle before claiming success — a PnP
                // re-enumeration could, in principle, have regenerated it (issue #15: never report a
                // success we haven't verified).
                var (present, current) = AdapterRenamer.ReadFriendlyName(deviceInstanceId);
                if (AdapterNameRules.FriendlyNameApplied(present, current, appliedName))
                    _ui.TryEnqueue(() => NativeMethods.Info(
                        "Adapter restarted. The new name should now appear everywhere.", AppName));
                else
                    _ui.TryEnqueue(() => NativeMethods.Warn(
                        "The adapter was restarted, but the name on disk is now " +
                        (present ? $"\"{current}\"" : "absent") +
                        $" instead of \"{appliedName}\" — Windows may have reset it. Try renaming again " +
                        "or reboot to re-apply.", AppName));
            }
            catch (Exception ex)
            {
                _ui.TryEnqueue(() => NativeMethods.Warn(
                    "The name was saved, but the adapter could not be restarted automatically:\n" +
                    $"{ex.Message}\n\nRestart the adapter manually or reboot to apply it.", AppName));
            }
        });
    }

    private async Task AddCurrentAsBridged()
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
            // Reuse the one open About window rather than stacking duplicates on repeated clicks.
            if (_aboutWindow is not null)
            {
                _aboutWindow.Activate();
                return;
            }

            var options = new BrandAboutOptions
            {
                Info = new AboutInfo
                {
                    AppName     = AppName,
                    Version     = AppInfo.Version,
                    Description = "Automatically connects Hyper-V VMs to the right virtual switch when the host changes networks. Manage VM power and state directly from the system tray.",
                    RepoUrl     = "https://github.com/0z00z0/HyperVManagerTray",
                    ExternalLibraries =
                    [
                        new ExternalLibrary("H.NotifyIcon.WinUI", "Dmitry Kolchev (HavenDV)", "System-tray icon + native context menu for WinUI 3", "MIT", "https://github.com/HavenDV/H.NotifyIcon"),
                    ],
                },
                // The shared window's "Check for Updates" reuses this class's own flow (which wraps
                // UpdatePrompt.RunAsync via NativeMethods.CaptureHwnd()); it returns false because the
                // Inno installer restarts the app itself, so no self-exit is needed. No update
                // machinery moved into the shared library.
                OnCheckForUpdates = CheckForUpdatesAsync,
            };

            _aboutWindow = new BrandAboutWindow(options);
            _aboutWindow.Closed += (_, _) => _aboutWindow = null;
            _aboutWindow.Activate();
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
