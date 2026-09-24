using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using HyperVManagerTray.Services;
using ZeroZero.Brand.Core;
using ZeroZero.Brand.WinUI;
using ZeroZero.Update;

namespace HyperVManagerTray.UI;

/// <summary>
/// The tray icon's right-click context menu.
///
/// H.NotifyIcon builds a native Win32 popup menu from <see cref="Flyout"/> on every right-click
/// and invokes each item's <c>Command</c> (the XAML <c>Click</c>/<c>Opening</c> events do NOT
/// fire for the native menu).  Items are created once with command bindings; <see cref="RefreshState"/>
/// resyncs the dynamic parts (override list, managed-VM list, the "Launch at startup" tick) right
/// before the menu opens.
/// </summary>
/// <remarks>
/// <para><b>The tray is the QUICK-COMMAND surface; Settings is the complete superset</b> (issue #34,
/// Espen's decision). That is the rule this menu's shape follows, and the reason each omission below is
/// deliberate rather than an oversight:</para>
/// <list type="bullet">
///   <item><b>No VM power verbs at all</b> — not even Start &amp; Connect. The dashboard is one LEFT-click
///         away, is state-aware, shows progress and reports failures; a native Win32 menu can do none of
///         those, and its power copies needed a cache-warm dance ("Loading VMs…") to even render. Dropping
///         them removed the deepest nesting (VM Power ▶ VM ▶ verb) in one stroke.</item>
///   <item><b>Repair host networking</b> → Settings → Maintenance: a recovery tool, not a quick command.</item>
///   <item><b>Add current network</b> → Settings → Network: it is configuration, and it belongs beside the
///         rules it creates. The live capture works identically from there.</item>
/// </list>
/// <para><b>Nesting is capped at two levels</b> (a top-level item, or one submenu of leaf items). The two
/// submenus that remain are lists, not hierarchies.</para>
/// <para><b>Unmanaged-VM discovery survives</b> and is what "Manage VMs" is built on — it is the only
/// reason <see cref="VmService.GetCachedVmsSync"/> is read here at all now.</para>
/// </remarks>
internal sealed class TrayMenu
{
    private readonly ConfigManager  _config;
    private readonly VmService      _vm;       // VM discovery (WMI) — feeds the Manage VMs list
    private readonly StartupManager _startup;
    private readonly AppUpdate      _update;
    private readonly NetworkActions _network;  // re-check / override (shared with Settings — issue #34)
    private readonly ManagedVmActions _managedVms;

    // The override's transience is stated up front in the label (issue #37): it is undone by the next
    // network change, which the UI documented nowhere — the confirmation balloon repeats it after the
    // fact, but a user browsing the menu deserves to know before they click.
    private readonly MenuFlyoutSubItem _overrideMenu   = new() { Text = "Override VM switch (until next network change)" };
    private readonly MenuFlyoutSubItem _manageVmsMenu  = new() { Text = "Manage VMs" };

    // "Run at Windows logon", as a checkmark. The tick is the only place this state is shown or
    // changed — the Settings window carries no startup control.
    private readonly ToggleMenuFlyoutItem _startupItem = new() { Text = "Launch at startup" };

    private MenuFlyoutItem? _updateBadge;
    private BrandAboutWindow? _aboutWindow;
    private SettingsWindow?   _settingsWindow;

    // Everything the Settings window needs but this menu no longer uses itself. Held only to construct
    // SettingsWindow lazily in ShowSettings — the tray is not the owner of these behaviours any more,
    // it is merely where the window is opened from.
    private readonly NetworkMonitor _monitor;
    private readonly HyperVManager  _hyperV;
    private readonly Action<string, string, bool> _notify;

    // An accessor, not the service: the broker session is composed after the tray icon is created, so a
    // value captured in this constructor would be null for the life of the process. Null still means
    // "not composed" — Settings renders its MQTT category without a live session rather than not at all.
    private readonly Func<MqttService?> _mqtt;

    /// <summary>UI dispatcher — captured on the UI thread in the constructor.</summary>
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _ui;

    public MenuFlyout Flyout { get; }

    public TrayMenu(ConfigManager config, NetworkMonitor monitor, HyperVManager hyperV, VmService vm,
                    StartupManager startup, AppUpdate update,
                    Action onExit, Action<string, string, bool> notify,
                    Func<MqttService?> mqtt)
    {
        _config        = config;
        _monitor       = monitor;
        _hyperV        = hyperV;
        _vm            = vm;
        _startup       = startup;
        _update        = update;
        _notify        = notify;
        _mqtt          = mqtt;
        _network       = new NetworkActions(config, monitor, hyperV, notify);
        _managedVms    = new ManagedVmActions(config, notify);

        _ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("TrayMenu must be created on the UI thread.");

        Flyout = new MenuFlyout();

        // The target state is the item's own tick flipped, not a fresh read of the scheduler: reading
        // again here would race the click against whatever the last background read left behind.
        _startupItem.Command = new RelayCommand(() =>
        {
            LogClick("Launch at startup");
            ToggleStartup(!_startupItem.IsChecked);
        });

        // The shape ChargeKeeper's menu uses (issue #46): Settings alone at the top, then the quick
        // commands, then the update check beside the startup tick, then About, then Exit — one group
        // each. See the class remarks for what is deliberately NOT among the quick commands.
        Add("Settings…", ShowSettings);
        Flyout.Items.Add(new MenuFlyoutSeparator());

        Add("Re-check network now", () => _ = _network.ReCheckNetworkAsync());
        Flyout.Items.Add(_overrideMenu);
        Flyout.Items.Add(_manageVmsMenu);
        Flyout.Items.Add(new MenuFlyoutSeparator());

        // "Check for updates" is NOT redundant with the "Update available" badge, and the two must not
        // be conflated: the badge appears only when a background check has ALREADY found a newer
        // version, and it jumps straight to the release page. This item is the user ASKING — it runs a
        // check now and reports the answer either way, including "you are up to date", which the badge
        // can never say (its absence is indistinguishable from "not checked yet").
        //
        // Settings → Maintenance → Updates keeps its own row; that is #34's Settings-is-the-superset
        // rule working as intended, not a duplicate. All three routes call the one flow below.
        Add("Check for updates", () => _ = CheckForUpdatesAsync());
        Flyout.Items.Add(_startupItem);
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
        RebuildManageVmsMenu();
        QueueStartupRefresh();
    }

    // ── Launch at startup ───────────────────────────────────────────────────────

    /// <summary>
    /// Re-reads the logon task and re-ticks the item. Off the UI thread: the scheduler read can take
    /// a couple of seconds, and the right-click path must not block on it. Every menu open starts one,
    /// and every change to the setting ends in one, so the tick tracks the task rather than a value
    /// captured once.
    /// </summary>
    private void QueueStartupRefresh() => Task.Run(() =>
    {
        var enabled = _startup.IsEnabled;   // never throws: absent, disabled and unreachable all read Off
        _ui.TryEnqueue(() => _startupItem.IsChecked = enabled);
    });

    /// <summary>
    /// Registers or deletes the logon task, off the UI thread because the write can block for seconds.
    /// A failure is reported and then re-read, so a write that did not take cannot leave the tick
    /// claiming it did.
    /// </summary>
    private void ToggleStartup(bool enable) => Task.Run(() =>
    {
        try
        {
            if (enable)
                _startup.Enable(Environment.ProcessPath
                    ?? throw new InvalidOperationException("Cannot determine executable path."));
            else
                _startup.Disable();
        }
        catch (Exception ex)
        {
            NativeMethods.Warn($"Could not change the startup setting:\n\n{ex.Message}", AppInfo.Name);
        }
        finally
        {
            QueueStartupRefresh();
        }
    });

    // ── Manage VMs ──────────────────────────────────────────────────────────────

    /// <summary>
    /// One flat, checkable list of every VM on the host: a checkmark means this app manages it. Clicking
    /// an unmanaged VM starts managing it; clicking a managed one stops (after the single confirmation
    /// <see cref="ManagedVmActions"/> owns).
    ///
    /// <para>This replaces the entire VM Power tree, and is a level shallower than what it replaces while
    /// keeping the two config actions Espen pinned to the tray. The checkmarks are genuine
    /// <see cref="ToggleMenuFlyoutItem"/>s — H.NotifyIcon's native-menu converter maps them to a checked
    /// Win32 popup item. No state is held in the item: the whole menu is rebuilt from config on every
    /// right-click, so a checkmark can never drift from what config.json says.</para>
    /// </summary>
    private void RebuildManageVmsMenu()
    {
        _manageVmsMenu.Items.Clear();

        var managed = _config.Current.VirtualMachines.Select(v => v.Ref).ToList();
        // Two VMs may share a name; the label then carries the start of each ID, so both can be told apart.
        var labels  = HostIdentity.Labels(
            managed.Select(v => (v.Id, v.Name))
                   .Concat((_vm.GetCachedVmsSync() ?? []).Select(d => (d.Id, d.Name))));

        // Read from the in-memory cache ONLY — never block the UI thread. GetCachedVmsSync() returns null
        // until the first background discovery completes; App.PreWarmVmCacheAsync owns that and calls
        // RefreshState() when the data lands.
        var allVms = _vm.GetCachedVmsSync();

        foreach (var vm in managed)
            _manageVmsMenu.Items.Add(ManagedItem(vm, labels.GetValueOrDefault(vm.Id) ?? vm.Shown));

        if (allVms is null)
        {
            // Cache still warming (the first few seconds after startup). The managed VMs are known from
            // config alone, so offer un-managing them; the unmanaged ones simply aren't discovered yet.
            if (_manageVmsMenu.Items.Count == 0)
                _manageVmsMenu.Items.Add(new MenuFlyoutItem { Text = "Loading VMs…", IsEnabled = false });
            return;
        }

        var unmanaged = VmConfigUi.UnmanagedVms(allVms.Select(d => new HostVm(d.Id, d.Name)), _config.Current.VirtualMachines);
        foreach (var host in unmanaged)
            if (allVms.FirstOrDefault(d => HostIdentity.Same(d.Id, host.Id)) is { } discovered)
                _manageVmsMenu.Items.Add(UnmanagedItem(discovered, labels.GetValueOrDefault(discovered.Id) ?? discovered.Name));

        if (_manageVmsMenu.Items.Count == 0)
            _manageVmsMenu.Items.Add(new MenuFlyoutItem { Text = "(no VMs found)", IsEnabled = false });

        // Kick off a background cache refresh so the *next* menu open is up-to-date.
        _ = Task.Run(async () =>
        {
            try { await _vm.RefreshOnceAsync().ConfigureAwait(false); }
            catch { /* non-fatal */ }
        });
    }

    /// <summary>A managed VM in the Manage VMs list: checked, and clicking un-manages it.</summary>
    private ToggleMenuFlyoutItem ManagedItem(VmRef vm, string label)
    {
        var item = new ToggleMenuFlyoutItem { Text = label, IsChecked = true };
        item.Command = new RelayCommand(() =>
        {
            UiActivityLog.Logger.LogInformation("Tray: Manage VMs → stop managing '{Vm}' ({Id})", vm.Shown, vm.Id);
            // Fire-and-forget is correct here: the native menu is already gone by the time the flow shows
            // its dialog, and it reports its own outcome.
            _ = _managedVms.RemoveAsync(vm);
        });
        return item;
    }

    /// <summary>A host VM this app does not manage: unchecked, and clicking starts managing it.</summary>
    private ToggleMenuFlyoutItem UnmanagedItem(DiscoveredVm vm, string label)
    {
        var item = new ToggleMenuFlyoutItem { Text = label, IsChecked = false };
        item.Command = new RelayCommand(() =>
        {
            UiActivityLog.Logger.LogInformation("Tray: Manage VMs → manage '{Vm}' ({Id})", vm.Name, vm.Id);
            _ = _managedVms.AddAsync(vm);
        });
        return item;
    }

    // ── Override VM switch ──────────────────────────────────────────────────────

    private void RebuildOverrideMenu()
    {
        _overrideMenu.Items.Clear();

        var switches   = VmConfigUi.OverrideSwitches(_config.Current.Fallback, _config.Current.Rules);
        var vms        = _config.Current.IdentifiedVms;
        var vmLabels   = HostIdentity.Labels(vms.Select(v => (v.Id, v.Name)));
        var swLabels   = HostIdentity.Labels(switches.Select(s => (s.Id, s.Name)));

        foreach (var vm in vms)
        {
            foreach (var sw in switches)
            {
                var vmRef = vm;
                var swRef = sw;
                _overrideMenu.Items.Add(new MenuFlyoutItem
                {
                    Text    = $"{vmLabels.GetValueOrDefault(vm.Id) ?? vm.Shown} → {swLabels.GetValueOrDefault(sw.Id) ?? sw.Shown}",
                    Command = new RelayCommand(() =>
                    {
                        UiActivityLog.Logger.LogInformation("Tray: Override switch '{Vm}' ({VmId}) → '{Switch}' ({SwitchId})",
                            vmRef.Shown, vmRef.Id, swRef.Shown, swRef.Id);
                        _ = _network.OverrideSwitchAsync(vmRef, swRef);
                    }),
                });
            }
        }

        // A managed VM is required for an override to mean anything — say so rather than showing an
        // empty submenu that reads as broken.
        if (_overrideMenu.Items.Count == 0)
            _overrideMenu.Items.Add(new MenuFlyoutItem { Text = "(no managed VMs)", IsEnabled = false });
    }

    // ── Update badge ────────────────────────────────────────────────────────────

    private async Task<bool> CheckForUpdatesAsync()
    {
        // No owner window is captured: the update window centres itself on the monitor under the
        // cursor and stays on top, so it survives the tray flyout being dismissed while the check
        // is still in flight. The flow must stay on the thread that owns this app's windows.
        await _update.RunManualAsync();
        // The installer (Inno Setup) closes and relaunches the app itself, so the shared About window
        // never needs to own the exit — always report "no self-exit required".
        return false;
    }

    /// <summary>
    /// Inserts (or updates) an "Update available" badge at the top of the tray menu.
    /// Safe to call more than once — subsequent calls only refresh the version text.
    /// Clicking the badge opens the GitHub releases page immediately; the full
    /// release-notes dialog is one item away, under "Check for updates" (issue #46).
    /// </summary>
    public void SetUpdateBadge(UpdateCheckResult result)
    {
        // The badge is the whole of what the silent check may produce, so it is gated on the one
        // outcome that means a newer release exists — never on a result merely having a release in it.
        if (result?.Outcome != UpdateCheckOutcome.UpdateAvailable || result.Release is not { } release) return;

        var text = $"⬆  Update available: v{release.VersionText}";
        if (_updateBadge is not null)
        {
            _updateBadge.Text = text;
            return;
        }

        // The badge opens the release page and nothing else. Downloading and running an installer is
        // reachable only from "Check for updates", where the user asked for it.
        var page = release.HtmlUri?.AbsoluteUri ?? string.Empty;
        _updateBadge = new MenuFlyoutItem
        {
            Text    = text,
            Command = new RelayCommand(() => { LogClick("Update badge → releases page"); Shell.Open(page); }),
        };

        // Badge + separator always sit above everything else in the menu.
        Flyout.Items.Insert(0, _updateBadge);
        Flyout.Items.Insert(1, new MenuFlyoutSeparator());
    }

    // ── Windows ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the consolidated Settings window (issue #18) — a reused singleton, mirroring the About
    /// window, so repeated clicks re-activate the one window instead of stacking duplicates. Public
    /// so the dashboard's own settings cog (issue #79) reuses this exact guard/construction path
    /// instead of a second one.
    /// </summary>
    public void ShowSettings()
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
            _settingsWindow = new SettingsWindow(_config, _update, _monitor, _hyperV,
                                                 _notify, _mqtt());
            _settingsWindow.Closed += (_, _) =>
            {
                UiActivityLog.Logger.LogInformation("Window: Settings closed");
                _settingsWindow = null;
            };
            _settingsWindow.Activate();
        });
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

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
                Info = AppAbout.CreateInfo(),
                // The shared window's "Check for Updates" reuses this class's own flow (which captures
                // the HWND and runs the manual update flow); it returns false because the Inno installer
                // restarts the app itself, so no self-exit is needed.
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
