using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using HyperVManagerTray.Helpers;
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
/// resyncs the dynamic parts (override list, managed-VM list) right before the menu opens.
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
    private readonly UpdateChecker  _updateChecker;
    private readonly NetworkActions _network;  // re-check / override (shared with Settings — issue #34)
    private readonly ManagedVmActions _managedVms;

    // The override's transience is stated up front in the label (issue #37): it is undone by the next
    // network change, which the UI documented nowhere — the confirmation balloon repeats it after the
    // fact, but a user browsing the menu deserves to know before they click.
    private readonly MenuFlyoutSubItem _overrideMenu   = new() { Text = "Override VM switch (until next network change)" };
    private readonly MenuFlyoutSubItem _manageVmsMenu  = new() { Text = "Manage VMs" };

    private MenuFlyoutItem? _updateBadge;
    private BrandAboutWindow? _aboutWindow;
    private SettingsWindow?   _settingsWindow;

    // Everything the Settings window needs but this menu no longer uses itself. Held only to construct
    // SettingsWindow lazily in ShowSettings — the tray is not the owner of these behaviours any more,
    // it is merely where the window is opened from.
    private readonly NetworkMonitor _monitor;
    private readonly HyperVManager  _hyperV;
    private readonly Action<string, string, bool> _notify;

    /// <summary>UI dispatcher — captured on the UI thread in the constructor.</summary>
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _ui;

    public MenuFlyout Flyout { get; }

    public TrayMenu(ConfigManager config, NetworkMonitor monitor, HyperVManager hyperV, VmService vm,
                    StartupManager startup, UpdateChecker updateChecker, Action onExit,
                    Action<string, string, bool> notify)
    {
        _config        = config;
        _monitor       = monitor;
        _hyperV        = hyperV;
        _vm            = vm;
        _startup       = startup;
        _updateChecker = updateChecker;
        _notify        = notify;
        _network       = new NetworkActions(config, monitor, hyperV, notify);
        _managedVms    = new ManagedVmActions(config, notify);

        _ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("TrayMenu must be created on the UI thread.");

        Flyout = new MenuFlyout();

        // ── The quick commands (see the class remarks for what is deliberately NOT here) ──
        Add("Re-check network now", () => _ = _network.ReCheckNetworkAsync());
        Flyout.Items.Add(_overrideMenu);
        Flyout.Items.Add(_manageVmsMenu);
        Flyout.Items.Add(new MenuFlyoutSeparator());

        // The window group, ordered as ChargeKeeper orders its own (issue #46): Settings…, then
        // Check for updates, then About…, then Exit. Each opens a window rather than acting on the
        // host, which is why "Check for updates" belongs here and not among the quick commands above.
        //
        // It is NOT redundant with the "Update available" badge, and the two must not be conflated:
        // the badge appears only when a background check has ALREADY found a newer version, and it
        // jumps straight to the release page. This item is the user ASKING — it runs a check now and
        // reports the answer either way, including "you are up to date", which the badge can never
        // say (its absence is indistinguishable from "not checked yet"). Restoring it fixes the
        // regression Espen reported: without it the tray offered no way to ask.
        //
        // Settings → Maintenance → Updates keeps its own row; that is #34's Settings-is-the-superset
        // rule working as intended, not a duplicate. All three routes call the one flow below.
        Add("Settings…",         ShowSettings);
        Add("Check for updates", () => _ = CheckForUpdatesAsync());
        Add("About…",            ShowAbout);
        Add("Exit",              onExit);

        RefreshState();
    }

    /// <summary>Re-reads live state into the dynamic menu parts. Call right before the menu opens.</summary>
    public void RefreshState()
    {
        RebuildOverrideMenu();
        RebuildManageVmsMenu();
    }

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

        var managed = _config.Current.VirtualMachines.Select(v => v.Name).ToList();

        // Read from the in-memory cache ONLY — never block the UI thread. GetCachedVmsSync() returns null
        // until the first background discovery completes; App.PreWarmVmCacheAsync owns that and calls
        // RefreshState() when the data lands.
        var allVms = _vm.GetCachedVmsSync();

        if (allVms is null)
        {
            // Cache still warming (the first few seconds after startup). The managed VMs are known from
            // config alone, so offer un-managing them; the unmanaged ones simply aren't discovered yet.
            foreach (var name in managed) _manageVmsMenu.Items.Add(VmItem(name, isManaged: true, nicName: ""));

            if (_manageVmsMenu.Items.Count == 0)
                _manageVmsMenu.Items.Add(new MenuFlyoutItem { Text = "Loading VMs…", IsEnabled = false });
            return;
        }

        var nicByVm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in allVms) nicByVm[d.Name] = d.NicName;

        foreach (var name in managed)
            _manageVmsMenu.Items.Add(VmItem(name, isManaged: true, nicName: ""));

        foreach (var name in VmConfigUi.UnmanagedVms(allVms.Select(d => d.Name), managed))
            _manageVmsMenu.Items.Add(VmItem(name, isManaged: false,
                nicName: nicByVm.TryGetValue(name, out var nic) ? nic : ""));

        if (_manageVmsMenu.Items.Count == 0)
            _manageVmsMenu.Items.Add(new MenuFlyoutItem { Text = "(no VMs found)", IsEnabled = false });

        // Kick off a background cache refresh so the *next* menu open is up-to-date.
        _ = Task.Run(async () =>
        {
            try { await _vm.RefreshOnceAsync().ConfigureAwait(false); }
            catch { /* non-fatal */ }
        });
    }

    /// <summary>One VM in the Manage VMs list. Checked ⇒ managed ⇒ clicking un-manages it, and vice versa.</summary>
    private ToggleMenuFlyoutItem VmItem(string vmName, bool isManaged, string nicName)
    {
        var item = new ToggleMenuFlyoutItem { Text = vmName, IsChecked = isManaged };
        item.Command = new RelayCommand(() =>
        {
            UiActivityLog.Logger.LogInformation(
                "Tray: Manage VMs → {Action} '{Vm}'", isManaged ? "stop managing" : "manage", vmName);
            // Fire-and-forget is correct here: the native menu is already gone by the time either flow
            // shows its dialog, and both report their own outcome.
            _ = isManaged ? _managedVms.RemoveAsync(vmName) : _managedVms.AddAsync(vmName, nicName);
        });
        return item;
    }

    // ── Override VM switch ──────────────────────────────────────────────────────

    private void RebuildOverrideMenu()
    {
        _overrideMenu.Items.Clear();

        var switches = VmConfigUi.OverrideSwitchNames(
            _config.Current.Fallback.VirtualSwitch,
            _config.Current.Rules.Select(r => r.VirtualSwitch));

        foreach (var vm in _config.Current.VirtualMachines)
        {
            foreach (var sw in switches)
            {
                var vmName = vm.Name;
                var swName = sw;
                _overrideMenu.Items.Add(new MenuFlyoutItem
                {
                    Text    = $"{vm.Name} → {sw}",
                    Command = new RelayCommand(() =>
                    {
                        UiActivityLog.Logger.LogInformation("Tray: Override switch '{Vm}' → '{Switch}'", vmName, swName);
                        _ = _network.OverrideSwitchAsync(vmName, swName);
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
    /// Clicking the badge opens the GitHub releases page immediately; the full
    /// release-notes dialog is one item away, under "Check for updates" (issue #46).
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

    // ── Windows ─────────────────────────────────────────────────────────────────

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
            _settingsWindow = new SettingsWindow(_config, _startup, _updateChecker, _monitor, _hyperV, _notify);
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
