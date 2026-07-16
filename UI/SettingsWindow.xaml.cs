using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using HyperVManagerTray.Services;
using ZeroZero.Brand.Core;
using ZeroZero.Brand.WinUI;

namespace HyperVManagerTray.UI;

/// <summary>
/// The consolidated Settings window — a left-nav <see cref="NavigationView"/> (issue #23) with one
/// category per pane item: General (run-on-startup, log level), Managed VMs (per-VM on-bridge-lost
/// action + delay), Network (the full rules editor + editable fallback switch/target-VMs — values that
/// were previously reachable only by hand-editing config.json), Adapters (rename a physical NIC's
/// description), Maintenance (open config/log, reload, check for updates), and About (embeds the shared
/// <see cref="BrandAboutControl"/> content inline rather than opening a second window).
///
/// <para>Everything persists through the existing <see cref="ConfigManager"/> (no parallel store).
/// Sections are built in code — the same code-first card idiom <see cref="DashboardWindow"/> uses — so
/// the app keeps one visual language without a separate settings-control dependency (zero new packages;
/// the NavigationView is in-box WinUI).</para>
///
/// <para>Window creation deliberately uses THIS app's proven native placement path
/// (<see cref="NativeMethods.GetCursorMonitorMetrics"/> + <see cref="AppWindow.Resize"/>/<see cref="AppWindow.Move"/>),
/// NOT <c>DisplayArea.FindAll</c> (which faulted on a multi-monitor host in the sibling app). Unlike the
/// tray's transient popups this is a real titled, resizable, taskbar-visible window that does NOT
/// auto-dismiss on focus loss, so the user can edit and alt-tab freely. Owned as a singleton by
/// <see cref="TrayMenu"/> (mirrors the About window).</para>
/// </summary>
internal sealed partial class SettingsWindow : Window
{
    // Sizing lives in WindowPlacement (Helpers) — derived from this window's real chrome and content
    // rather than picked round, and unit-tested there without a GUI. See issue #31.

    private readonly ConfigManager    _config;
    private readonly StartupManager   _startup;
    private readonly UpdateChecker    _updateChecker;
    private readonly AdapterRenameFlow _renameFlow;
    private readonly DispatcherQueue  _ui;

    // The live network commands and the managed-VM add/remove, shared verbatim with the tray (issue #34):
    // this window is the COMPLETE surface, so it holds both the actions the tray also offers as quick
    // commands (re-check, override) and the ones that now live only here (repair, add current network,
    // and — per issue #47 — creating and deleting a managed VM at all).
    private readonly NetworkActions   _network;
    private readonly ManagedVmActions _managedVms;

    // Suppresses commit handlers while controls are populated programmatically (same re-entrancy
    // guard idiom as the sibling app's settings window). One flag is safe: every Load runs
    // synchronously to completion before anything else can fire.
    private bool _updating;

    // Set once the user physically flips the startup toggle, so the initial background schtasks read
    // (which finishes ~1-2 s later) can't clobber a deliberate user action with a now-stale value.
    private bool _userToggledStartup;

    // Set in the Closed handler so any in-flight background callback (schtasks read, adapter
    // enumeration, an error toast) early-returns instead of touching a torn-down window.
    private bool _closed;

    private ComboBox?     _logLevelCombo;
    private ToggleSwitch? _startupToggle;

    // Network rules editor (issue #23). _workingRules is the UI's authoritative copy while the window is
    // open (deep copies of config rules); edits mutate it in place and persist via _config.SaveRules, so
    // a reload-driven re-sort of _config.Current can't reorder controls mid-edit.
    private readonly List<NetworkRule> _workingRules = [];
    private StackPanel? _rulesListPanel;
    private ComboBox?   _fallbackSwitchCombo;
    private TextBox?    _fallbackVmsBox;

    // ── Live host values for the identity pickers (issue #41) ────────────────────
    //
    // The last successful enumeration of the host's switches, VMs, per-VM NICs and physical adapters.
    // Held in a FIELD, not on the controls, for the same reason as _reloadResultMessage: a config reload
    // rebuilds every section from scratch, discarding whatever was written onto the old controls. With
    // the snapshot cached here the fresh controls are re-populated instantly from it, so a rebuild never
    // silently downgrades a working picker back to a bare text box while WMI is re-queried.
    // Null = never enumerated yet (pickers are empty ⇒ plain free-text boxes, which is the correct and
    // fully-functional degraded state — see HostInventory's remarks).
    private HostInventory.Snapshot? _inventory;

    // One callback per control that wants live values, registered as the control is built. The callbacks
    // capture their controls, so a list that is not cleared when its controls are replaced grows forever
    // and re-populates detached ones.
    //
    // TWO lists because the two rebuild scopes have different lifetimes: BuildSections replaces
    // everything, but RebuildRuleCards replaces ONLY the rule cards (Add/Remove rule call it directly,
    // with the rest of the window untouched) — so a single list would either leak on every Add, or lose
    // every other section's pickers. _consumerSink is whichever list is currently being filled.
    private readonly List<Action<HostInventory.Snapshot>> _sectionConsumers = [];
    private readonly List<Action<HostInventory.Snapshot>> _ruleConsumers    = [];
    private List<Action<HostInventory.Snapshot>> _consumerSink;

    // The single source of truth mapping each nav item's Tag → its content panel. Built once by
    // BuildSections (after the XAML fields are assigned), it drives OnNavSelectionChanged so a 7th
    // category can't be half-wired, AND doubles as the "panels are ready" guard: while it is null an
    // early SelectionChanged (which fires during InitializeComponent, before the panels exist) is
    // ignored instead of NRE-ing out of the constructor (finding 6, cleanup 13).
    private IReadOnlyDictionary<string, StackPanel>? _panels;

    /// <param name="notify">
    /// The tray balloon (title, message, isError). Passed in rather than replaced with dialogs of this
    /// window's own: the outcome of a re-check or an override reads identically wherever it was started
    /// from, and inventing a second display vocabulary for the same events is what issue #37 spent its
    /// effort undoing.
    /// </param>
    public SettingsWindow(ConfigManager config, StartupManager startup, UpdateChecker updateChecker,
                          NetworkMonitor monitor, HyperVManager hyperV,
                          Action<string, string, bool> notify)
    {
        _consumerSink  = _sectionConsumers;   // RebuildRuleCards swaps this while it builds
        _config        = config;
        _startup       = startup;
        _updateChecker = updateChecker;

        InitializeComponent();
        Title = $"{AppInfo.Name} — Settings";

        _ui = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("SettingsWindow must be created on the UI thread.");
        _renameFlow = new AdapterRenameFlow(_config, _ui);
        _network    = new NetworkActions(config, monitor, hyperV, notify);
        _managedVms = new ManagedVmActions(config, notify);

        Closed += OnClosed;

        // NOTHING below may throw out of the constructor: the caller only assigns the singleton and
        // calls Activate() once the ctor returns, so a throw here would leave an orphaned, never-shown
        // window (the "Settings never appears" failure the sibling app hit on a multi-monitor host).
        // Each step is best-effort and degrades independently.
        SafeInit(nameof(ConfigureChrome), ConfigureChrome);
        SafeInit(nameof(BuildSections),   BuildSections, onFail: ShowLoadFailurePlaceholder);
    }

    private static void SafeInit(string step, Action body, Action? onFail = null)
    {
        try { body(); }
        catch (Exception ex)
        {
            AppInfo.AppendCrashLogLine("SettingsWindow", $"ctor step '{step}': {ex}");
            try { onFail?.Invoke(); } catch { /* the fallback must never throw out of the ctor either */ }
        }
    }

    // Leaves the user with a visible explanation instead of a blank window if section building throws
    // part-way. Uses no theme brushes, so it can't itself fault; adds no window activation, so it can't
    // reintroduce the orphaned-never-shown-window risk SafeInit exists to prevent.
    private void ShowLoadFailurePlaceholder() => GeneralPanel.Children.Add(new TextBlock
    {
        Text         = "Settings could not be fully loaded. See the crash log for details.",
        TextWrapping = TextWrapping.Wrap,
        Margin       = new Thickness(0, 8, 0, 0),
    });

    /// <summary>
    /// Tears down the window's handlers and persists its final on-screen rect to config.json (issue
    /// #31).  <see cref="_closed"/> is set FIRST so nothing this triggers can rebuild a window that is
    /// going away, and the whole save is guarded: nothing may throw out of a <c>Closed</c> handler, and
    /// a lost window rect is cosmetic — never worth taking the app down for.
    /// </summary>
    private void OnClosed(object sender, WindowEventArgs e)
    {
        _closed = true;
        if (_startupToggle is not null) _startupToggle.Toggled         -= OnStartupToggled;
        if (_logLevelCombo is not null) _logLevelCombo.SelectionChanged -= OnLogLevelChanged;

        try
        {
            // Only a restored window has a meaningful rect. A minimized one reports a position of
            // roughly (-32000,-32000), and a maximized one a size that is really the screen's — saving
            // either would reopen the window somewhere the user never put it.
            if (AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Restored })
                return;

            var pos  = AppWindow.Position;
            var size = AppWindow.Size;
            _config.SaveSettingsWindowRect(new WindowRect(pos.X, pos.Y, size.Width, size.Height));
        }
        catch (Exception ex) { AppInfo.AppendCrashLogLine("SettingsWindow", $"save window rect: {ex}"); }
    }

    // Toggles which category panel is visible. Guarded against firing during InitializeComponent (the
    // General item's IsSelected="True" raises this before the content panels' backing fields are set)
    // and against a torn-down window.
    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // _panels is null until BuildSections has run, so an early fire during InitializeComponent (the
        // General item's IsSelected="True" raises this before every panel field is set) is ignored
        // rather than dereferencing an unset panel and NRE-ing out of the ctor (finding 6).
        if (_closed || _panels is null) return;
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag }) return;

        foreach (var (panelTag, panel) in _panels)
            panel.Visibility = panelTag == tag ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Window chrome / placement ────────────────────────────────────────────────

    private void ConfigureChrome()
    {
        AppWindow.IsShownInSwitchers = true;

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: true);
        presenter.IsResizable   = true;
        presenter.IsMaximizable = true;
        presenter.IsMinimizable = true;
        presenter.IsAlwaysOnTop = false;

        // Issue #31 (2): without these the window could be dragged arbitrarily small, which is what
        // triggered the row collapse. OverlappedPresenter's minimums are applied in DIP — the platform
        // scales them by the window's CURRENT monitor DPI, which is also why they are set once here and
        // stay correct when the window is dragged to a different-DPI screen.
        presenter.PreferredMinimumWidth  = WindowPlacement.SettingsMinWidth;
        presenter.PreferredMinimumHeight = WindowPlacement.SettingsMinHeight;
        AppWindow.SetPresenter(presenter);

        // Native, single-monitor placement — the same MonitorFromPoint path every other window in
        // this app uses. No DisplayArea.FindAll (see the class remarks).
        var rect = ComputeInitialRect();
        try { AppWindow.MoveAndResize(rect); }
        catch (Exception ex) { AppInfo.AppendCrashLogLine("SettingsWindow", $"MoveAndResize: {ex}"); }

        // Issue #36: this is the app's ONLY window with a real title bar (every other one is a frameless
        // popup), so it is the only one that showed a default-coloured bar over the Mica BaseAlt backdrop
        // and a generic icon in the taskbar / Alt-Tab. TitleBarTheme guards each step internally and
        // cannot throw — chrome must never cost us the window (see SafeInit).
        //
        // Fully qualified deliberately: Microsoft.UI.Windowing (imported above for OverlappedPresenter)
        // ships its OWN TitleBarTheme type, so the bare name is ambiguous. The sibling app qualifies its
        // call the same way, for the same reason.
        Helpers.TitleBarTheme.Apply(AppWindow, (Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default);
    }

    /// <summary>
    /// The window's opening rect (physical px): the rect saved in config.json when there is a complete
    /// one — clamped onto a currently-connected monitor, so a rect saved on a since-unplugged screen
    /// can't strand the window offscreen — otherwise a default centred on the monitor under the cursor
    /// and capped to its work area, so it is never oversized on a small screen (issue #31 (1)).
    /// </summary>
    private RectInt32 ComputeInitialRect()
    {
        var cfg = _config.Current;
        if (WindowPlacement.TryGetSavedRect(
                cfg.SettingsWindowX, cfg.SettingsWindowY,
                cfg.SettingsWindowWidth, cfg.SettingsWindowHeight) is { } saved)
        {
            // The minimums are passed in DIP and scaled inside by the DPI of the monitor the saved rect
            // actually lands on — not the cursor's — so a rect saved on the 100 % external screen isn't
            // floored using the 175 % laptop's scale on a mixed-DPI desktop.
            var (x, y, w, h, _) = NativeMethods.ClampRectToNearestMonitor(
                saved.X, saved.Y, saved.Width, saved.Height,
                WindowPlacement.SettingsMinWidth, WindowPlacement.SettingsMinHeight);
            return new RectInt32(x, y, w, h);
        }

        var (work, scale) = NativeMethods.GetCursorMonitorMetrics();
        var d = WindowPlacement.DefaultRect(
            work.Left, work.Top, work.Right, work.Bottom, scale,
            WindowPlacement.SettingsDefaultWidth, WindowPlacement.SettingsDefaultHeight);
        return new RectInt32(d.X, d.Y, d.Width, d.Height);
    }

    // ── Section composition ──────────────────────────────────────────────────────

    private void BuildSections()
    {
        VersionText.Text = $"v{AppInfo.Version}";

        // Wire the Tag→panel map once the XAML fields are guaranteed assigned (we're past
        // InitializeComponent). Tags must match the NavigationViewItem Tags in SettingsWindow.xaml.
        // Also flips OnNavSelectionChanged's readiness guard on (see the field's remarks).
        // Drop the previous build's picker callbacks before any new control can register one — they
        // capture controls that are about to be detached (issue #41). The rule cards' own list is cleared
        // by RebuildRuleCards, which BuildNetworkSection calls below.
        _sectionConsumers.Clear();

        _panels = new Dictionary<string, StackPanel>(StringComparer.Ordinal)
        {
            ["General"]     = GeneralPanel,
            ["Vms"]         = VmsPanel,
            ["Network"]     = NetworkPanel,
            ["Adapters"]    = AdaptersPanel,
            ["Maintenance"] = MaintenancePanel,
            ["About"]       = AboutPanel,
        };

        GeneralPanel.Children.Clear();
        GeneralPanel.Children.Add(BuildGeneralSection());

        VmsPanel.Children.Clear();
        VmsPanel.Children.Add(BuildVmSection());

        NetworkPanel.Children.Clear();
        NetworkPanel.Children.Add(BuildNetworkSection());

        AdaptersPanel.Children.Clear();
        AdaptersPanel.Children.Add(BuildAdaptersSection());

        MaintenancePanel.Children.Clear();
        MaintenancePanel.Children.Add(BuildMaintenanceSection());

        AboutPanel.Children.Clear();
        AboutPanel.Children.Add(BuildAboutSection());

        // Every section is now built and every picker has registered. Fill them from the cached snapshot
        // first so a rebuild loses nothing, THEN re-read the host in the background so a switch or VM
        // created since the window opened shows up. Both are no-ops on a first build with no host.
        if (_inventory is not null) ApplyInventory(_inventory);
        LoadHostInventoryAsync();
    }

    // ── Live host values (issue #41) ─────────────────────────────────────────────

    /// <summary>
    /// Enumerates the host off the UI thread and hands the result to every registered picker.
    ///
    /// <para>This is the load-bearing thread rule of this issue: <see cref="HostInventory.Read"/> connects
    /// to WMI and can stall for seconds on a degraded host, and Settings opening must NEVER wait on it —
    /// the same reasoning as <see cref="LoadAdaptersAsync"/> (which this call now subsumes, so the window
    /// makes ONE adapter enumeration rather than one per feature that wants the list). Nothing here can
    /// fail loudly: HostInventory never throws, and an empty snapshot simply leaves every picker
    /// suggestion-less — i.e. exactly the free-text box each one replaced.</para>
    /// </summary>
    private void LoadHostInventoryAsync() => Task.Run(() =>
    {
        var snapshot = HostInventory.Read();
        _ui.TryEnqueue(() =>
        {
            if (_closed) return;
            _inventory = snapshot;
            ApplyInventory(snapshot);
        });
    });

    /// <summary>
    /// Pushes a snapshot into every registered picker. UI thread only. Each consumer is guarded
    /// individually — one picker throwing must not leave the rest unpopulated — and the whole pass runs
    /// with the re-entrancy guard held, because populating a control's items raises the very
    /// SelectionChanged/TextChanged handlers that commit to config: without this, merely enumerating the
    /// host would write config.json, and a config write raises ConfigReloaded, which re-evaluates the
    /// network and can move a VM's switch. Opening Settings must not touch the network.
    /// </summary>
    private void ApplyInventory(HostInventory.Snapshot snapshot) => WithUpdatingSuppressed(() =>
    {
        foreach (var consumer in _sectionConsumers.Concat(_ruleConsumers))
        {
            try { consumer(snapshot); }
            catch (Exception ex) { AppInfo.AppendCrashLogLine("SettingsWindow", $"ApplyInventory: {ex}"); }
        }
    });

    /// <summary>
    /// An editable picker for an identity field: it SUGGESTS the live values but accepts anything typed.
    ///
    /// <para>Editable — not a closed dropdown — is the whole design decision of issue #41. A rule is
    /// legitimately written before the switch or VM it names exists, and the host may be offline or
    /// Hyper-V unreachable when Settings is opened; a closed list would make those cases uneditable. So
    /// the live values are an affordance that removes the retyping (and with it the silent-typo failure
    /// mode), never a constraint. With no items it is precisely the TextBox it replaced.</para>
    ///
    /// <para><paramref name="live"/> pulls this field's values out of a snapshot; it is invoked on each
    /// enumeration, under the re-entrancy guard.</para>
    /// </summary>
    private ComboBox SuggestionCombo(
        string? value,
        string placeholder,
        Func<HostInventory.Snapshot, IReadOnlyList<string>> live,
        Action<string> commit)
    {
        var combo = new ComboBox
        {
            IsEditable          = true,
            Text                = value ?? "",
            PlaceholderText     = placeholder,
            MinWidth            = 220,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        _consumerSink.Add(snapshot =>
        {
            // The user's current text is the authority, not the list: re-populating must never retype the
            // field. Captured before and restored after, because assigning ItemsSource clears Text.
            var current = combo.Text;
            combo.ItemsSource = SettingsOptions.SuggestionItems(current, live(snapshot));
            combo.Text        = current;
        });

        // Both paths, deliberately. LostFocus alone would lose a pick made just before the window is
        // closed or the app is alt-tabbed away; SelectionChanged alone would miss free text, which is the
        // case that must keep working. Committing twice is harmless — every ConfigManager mutator
        // no-ops when the value is unchanged, so the redundant call never reaches the file.
        combo.SelectionChanged += (_, _) =>
        {
            if (_updating) return;
            commit(combo.SelectedItem as string ?? combo.Text);
        };
        combo.LostFocus += (_, _) =>
        {
            if (_updating) return;
            commit(combo.Text);
        };

        // NOT optional, and the reason this control can be trusted with free text at all: an editable
        // ComboBox raises TextSubmitted when the user commits text that matches no item, and its DEFAULT
        // handling is to REVERT the box to the last selected value. That default is exactly backwards
        // here — text matching no item is the case this field must support (a switch or VM that doesn't
        // exist yet, or a host that couldn't be enumerated), so silently discarding it would reintroduce
        // the closed-picklist behaviour this issue exists to avoid. Handled = true suppresses the revert
        // and keeps what the user typed.
        combo.TextSubmitted += (_, args) =>
        {
            args.Handled = true;
            if (_updating) return;
            commit(combo.Text);
        };

        return combo;
    }

    /// <summary>
    /// A "pick from the live list" button that fills a field the user may equally well type into. Used
    /// where the field is not itself a combo — the MAC box (whose validation gating must stay exactly as
    /// it was) and the one-VM-per-line target boxes (where picking must ADD a line, not replace the text).
    /// Disabled and self-explaining while there is nothing to offer, so it can never look broken on a
    /// host that couldn't be enumerated.
    /// </summary>
    private DropDownButton PickerButton(
        string content,
        string emptyHint,
        Func<HostInventory.Snapshot, IReadOnlyList<(string Label, string Value)>> live,
        Action<string> picked)
    {
        var button = new DropDownButton { Content = content, IsEnabled = false };
        var menu   = new MenuFlyout();
        button.Flyout = menu;
        menu.Items.Add(new MenuFlyoutItem { Text = emptyHint, IsEnabled = false });

        _consumerSink.Add(snapshot =>
        {
            var options = live(snapshot);
            menu.Items.Clear();
            if (options.Count == 0)
            {
                menu.Items.Add(new MenuFlyoutItem { Text = emptyHint, IsEnabled = false });
                button.IsEnabled = false;
                return;
            }

            foreach (var (label, value) in options)
            {
                var v = value;   // capture per iteration
                menu.Items.Add(new MenuFlyoutItem { Text = label, Command = new RelayCommand(() => picked(v)) });
            }
            button.IsEnabled = true;
        });

        return button;
    }

    // ── General ──────────────────────────────────────────────────────────────────

    private UIElement BuildGeneralSection()
    {
        var panel = Section("General");

        // Run on startup — the tray toggle moved here. IsEnabled reflects the scheduled task, read
        // off the UI thread (schtasks can take up to a couple of seconds).
        _startupToggle = new ToggleSwitch { OnContent = "On", OffContent = "Off" };
        _startupToggle.Toggled += OnStartupToggled;
        // The description says what the SETTING does, not what the control is doing while it loads
        // (issue #42): "Off until the status loads." leaked this window's own async read into a
        // sentence meant to explain the feature. The brief pre-load Off state is not a lie the user
        // needs warning about — LoadStartupStateAsync corrects it in place, and _userToggledStartup
        // makes a toggle during the load win rather than be clobbered.
        panel.Children.Add(SettingRow(
            "Run on startup",
            "Start automatically at sign-in (elevated scheduled task).",
            _startupToggle));
        LoadStartupStateAsync();

        // Log level — previously only editable by hand in config.json.
        _logLevelCombo = new ComboBox { MinWidth = 200 };
        WithUpdatingSuppressed(() => PopulateLabelCombo(
            _logLevelCombo, SettingsOptions.LogLevels, SettingsOptions.LogLevelToIndex(_config.Current.LogLevel)));
        _logLevelCombo.SelectionChanged += OnLogLevelChanged;
        panel.Children.Add(SettingRow(
            "Log level",
            // The logging lane makes this govern every log file, not just switcher.log (issue #24).
            "Minimum severity written to all of the app's log files. Debug captures diagnostic detail; None disables logging.",
            _logLevelCombo));

        return panel;
    }

    private void LoadStartupStateAsync() => Task.Run(() =>
    {
        bool enabled;
        try   { enabled = _startup.IsEnabled; }
        catch { enabled = false; }
        _ui.TryEnqueue(() =>
        {
            // Don't clobber a torn-down window, nor a value the user has since set by hand.
            if (_closed || _userToggledStartup) return;
            WithUpdatingSuppressed(() =>
            {
                if (_startupToggle is not null) _startupToggle.IsOn = enabled;
            });
        });
    });

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_updating || _startupToggle is null) return;
        _userToggledStartup = true;   // a real user action — the initial load must not overwrite it
        bool wanted = _startupToggle.IsOn;
        Task.Run(() =>
        {
            try
            {
                if (wanted)
                    _startup.Enable(Environment.ProcessPath
                        ?? throw new InvalidOperationException("Cannot determine executable path."));
                else
                    _startup.Disable();
            }
            catch (Exception ex)
            {
                _ui.TryEnqueue(() =>
                {
                    if (_closed) return;
                    NativeMethods.Warn(
                        $"Could not change the startup setting:\n\n{ex.Message}", AppInfo.Name);
                });
            }

            // Re-sync to the task's real state (the write may have failed or been overridden).
            bool actual;
            try   { actual = _startup.IsEnabled; }
            catch { actual = false; }
            _ui.TryEnqueue(() =>
            {
                if (_closed) return;
                WithUpdatingSuppressed(() =>
                {
                    if (_startupToggle is not null) _startupToggle.IsOn = actual;
                });
            });
        });
    }

    private void OnLogLevelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || _logLevelCombo is null || _logLevelCombo.SelectedIndex < 0) return;
        var level = SettingsOptions.IndexToLogLevel(_logLevelCombo.SelectedIndex);
        Task.Run(() =>
        {
            try { _config.UpdateLogLevel(level); }
            catch (Exception ex)
            {
                _ui.TryEnqueue(() =>
                {
                    if (_closed) return;
                    NativeMethods.Warn(
                        $"Could not save the log level:\n\n{ex.Message}", AppInfo.Name);
                });
            }
        });
    }

    // ── Managed VMs (on-bridge-lost action) ──────────────────────────────────────

    private UIElement BuildVmSection()
    {
        var panel = Section("Managed VMs");
        panel.Children.Add(Description(
            "The VMs this app looks after: which network adapter each one is reconnected through, and " +
            "what to do with the VM when the bridged network is lost (the switch falls back to the " +
            "default). The action is cancelled if the bridge returns within the delay."));

        var vms = _config.Current.VirtualMachines;
        if (vms.Count == 0)
        {
            // Issue #47's acceptance: this no longer sends the user to the tray as the ONLY route — the
            // add control is right below. The tray is mentioned as the alternative it now is (issue #38).
            panel.Children.Add(Card(new TextBlock
            {
                Text         = "No VMs are managed yet. Add one below, or from the tray's Manage VMs menu.",
                TextWrapping = TextWrapping.Wrap,
                Opacity      = 0.75,
            }));
        }
        else
        {
            foreach (var vm in vms)
                panel.Children.Add(BuildVmCard(vm));
        }

        panel.Children.Add(BuildAddVmCard());
        return panel;
    }

    /// <summary>
    /// "Start managing a VM" — the half of issue #47 that made Settings genuinely complete. Creating a
    /// managed VM was previously reachable ONLY from the tray, which is what broke Espen's standing rule
    /// that Settings is the superset (issue #34); nothing else in <see cref="AppConfig"/> was tray-only.
    ///
    /// <para>An editable picker, following the same reasoning as every other identity field (issue #41):
    /// it SUGGESTS the host's unmanaged VMs — reusing the ONE cold <see cref="HostInventory"/> read this
    /// window already makes, rather than adding a third enumeration path — but accepts free text, because
    /// a config may legitimately name a VM that has not been created yet, and the host may be unreachable
    /// when Settings is opened. With no suggestions it is exactly the text box it would otherwise be.</para>
    ///
    /// <para>Deliberately NOT a <see cref="SuggestionCombo"/>: that control commits on
    /// SelectionChanged/LostFocus, which is right for a field that edits an existing VM and quite wrong
    /// here — merely tabbing past this box must not add a VM to config. The Add button is the only commit.</para>
    /// </summary>
    private UIElement BuildAddVmCard()
    {
        var combo = new ComboBox
        {
            IsEditable          = true,
            PlaceholderText     = "VM name",
            MinWidth            = 220,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        // An editable ComboBox REVERTS text that matches no item unless TextSubmitted is handled — the
        // same trap SuggestionCombo documents at length. A not-yet-created VM is exactly the case that
        // must keep working, so suppress the revert.
        combo.TextSubmitted += (_, args) => args.Handled = true;

        var addBtn = new Button { Content = "Start managing" };

        _consumerSink.Add(snapshot =>
        {
            var current = combo.Text;   // assigning ItemsSource clears Text — restore what the user typed
            combo.ItemsSource = VmConfigUi.UnmanagedVms(
                snapshot.VmNames, _config.Current.VirtualMachines.Select(v => v.Name));
            combo.Text = current;
        });

        addBtn.Click += async (_, _) =>
        {
            var name = (combo.SelectedItem as string ?? combo.Text)?.Trim();
            if (string.IsNullOrEmpty(name)) return;

            // The VM's own adapter, when the host could be read and reports one — the same value the tray
            // passes. Blank is fine: AddVmToConfig falls back to the Hyper-V default ("Network Adapter"),
            // and the NIC row on the card that appears is there to correct it.
            var nic = _inventory?.NicNamesFor(name).FirstOrDefault() ?? "";

            if (!await _managedVms.AddAsync(name, nic)) return;
            if (_closed) return;
            // The VM list is built from _config.Current, which AddAsync has just confirmed — rebuild so
            // the new card appears. Same path the Reload button uses; every populate is guarded, so the
            // rebuild itself commits nothing.
            RefreshValuesFromConfig();
        };

        var stack = new StackPanel { Spacing = 6, MinWidth = 220 };
        stack.Children.Add(combo);
        stack.Children.Add(addBtn);

        return SettingRow(
            "Start managing a VM",
            "Offers the VMs on this host that aren't managed yet; any name can still be typed (a VM that "
            + "does not exist yet is allowed). The VM is not started or changed — only this app's list.",
            stack);
    }

    /// <summary>
    /// One managed VM: its network adapter (issue #41) and its on-bridge-lost action + delay.
    ///
    /// <para>A Card holding the VM's name over two <see cref="SettingRowPanel"/> rows, rather than the
    /// single row this was: the NIC name is a second, unrelated setting and deserves its own labelled row
    /// with its own description. Reusing SettingRowPanel (not a Grid) keeps issue #31's guarantee — each
    /// row drops its control beneath the text when the window is too narrow for both — which a Grid would
    /// have silently thrown away, and which issue #44's wider font makes matter more, not less.</para>
    /// </summary>
    private UIElement BuildVmCard(VmTarget vm)
    {
        var vmName = vm.Name;

        var content = new StackPanel { Spacing = 10 };

        // Header: the VM's name, and the only way to stop managing it from here (issue #47). "Stop
        // managing" rather than "Remove": next to a VM name, "remove" reads as "delete the virtual
        // machine", and this deletes nothing — ManagedVmActions' single confirmation says so in full.
        var title = new TextBlock
        {
            Text              = vmName,
            FontSize          = 14,
            FontWeight        = FontWeights.SemiBold,
            TextWrapping      = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var stopBtn = new Button { Content = "Stop managing" };
        stopBtn.Click += async (_, _) =>
        {
            if (!await _managedVms.RemoveAsync(vmName)) return;   // cancelled, or not confirmed — say nothing more
            if (_closed) return;
            RefreshValuesFromConfig();   // this card's VM is gone from _config.Current — re-render without it
        };

        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(title, 0);
        Grid.SetColumn(stopBtn, 1);
        header.Children.Add(title);
        header.Children.Add(stopBtn);
        content.Children.Add(header);

        // ── Network adapter (issue #41) ──
        // Previously reachable ONLY by hand-editing config.json — a VM with a renamed or second synthetic
        // adapter silently never reconnected, and the file was the only fix. The suggestions are the VM's
        // OWN adapters as Hyper-V reports them; free text stays valid because the VM may not exist yet.
        var nicCombo = SuggestionCombo(
            vm.NicName,
            SettingsOptions.DefaultNicName,
            snapshot => snapshot.NicNamesFor(vmName),
            nic => Task.Run(() =>
            {
                try { _config.SetVmNicName(vmName, nic); }
                catch (Exception ex) { WarnOnUi($"Could not save the network adapter for {vmName}:\n\n{ex.Message}"); }
            }));
        content.Children.Add(Row(
            "Network adapter",
            $"The VM adapter this app reconnects. Offers {vmName}'s own adapters when the host can be "
            + $"read; any name can still be typed. Blank restores the default (\"{SettingsOptions.DefaultNicName}\").",
            nicCombo));

        // ── On bridge lost ──
        var actionCombo = new ComboBox { MinWidth = 150 };
        var delayCombo  = new ComboBox { MinWidth = 130 };

        WithUpdatingSuppressed(() =>
        {
            PopulateLabelCombo(actionCombo, SettingsOptions.BridgeLostActions,
                SettingsOptions.BridgeLostActionToIndex(vm.OnBridgeLostAction));

            LoadDelayCombo(delayCombo, SettingsOptions.NormalizeDelaySeconds(vm.OnBridgeLostDelaySeconds));
            delayCombo.IsEnabled = SettingsOptions.NormalizeBridgeLostAction(vm.OnBridgeLostAction) is not null;
        });

        void Commit()
        {
            if (_updating) return;
            var act   = SettingsOptions.IndexToBridgeLostAction(actionCombo.SelectedIndex);
            int delay = delayCombo.SelectedItem is ComboBoxItem { Tag: int d } ? d : 30;
            delayCombo.IsEnabled = act is not null;
            Task.Run(() =>
            {
                try { _config.SetVmBridgeLostAction(vmName, act, delay); }
                catch (Exception ex) { WarnOnUi($"Could not save the setting for {vmName}:\n\n{ex.Message}"); }
            });
        }

        actionCombo.SelectionChanged += (_, _) => Commit();
        delayCombo.SelectionChanged  += (_, _) => Commit();

        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        controls.Children.Add(actionCombo);
        controls.Children.Add(delayCombo);
        content.Children.Add(Row("On bridge lost", "Action · delay when the bridge is lost", controls));

        return Card(content);
    }

    /// <summary>
    /// Populates a delay picker with the presets and selects the one matching <paramref name="current"/>;
    /// a stored non-preset value (a hand-edited config) is inserted as a custom entry and selected, so a
    /// user's value is shown, never silently overwritten. Each item's Tag holds the int seconds.
    /// </summary>
    private static void LoadDelayCombo(ComboBox combo, int current)
    {
        combo.Items.Clear();
        foreach (var seconds in SettingsOptions.BridgeLostDelaySeconds)
            combo.Items.Add(new ComboBoxItem { Content = SettingsOptions.FormatDelay(seconds), Tag = seconds });
        if (!SettingsOptions.BridgeLostDelaySeconds.Contains(current))
            combo.Items.Insert(0, new ComboBoxItem { Content = SettingsOptions.FormatDelay(current), Tag = current });
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().First(i => (int)i.Tag! == current);
    }

    // ── Network (rules editor + fallback) ─────────────────────────────────────────

    private UIElement BuildNetworkSection()
    {
        var panel = Section("Network");
        panel.Children.Add(Description(
            "Rules map a recognised host network (by adapter MAC and/or IP subnet) to the Hyper-V " +
            "virtual switch the listed VMs should use. Rules are evaluated by ascending priority; the " +
            "first match wins. When none match, the fallback switch is used."));

        // Snapshot config into the working list the editor mutates.
        _workingRules.Clear();
        foreach (var r in _config.Current.Rules) _workingRules.Add(ConfigManager.CleanRule(r));

        panel.Children.Add(new TextBlock
        {
            Text = "Rules", FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 6, 0, 0),
        });

        _rulesListPanel = new StackPanel { Spacing = 10 };
        RebuildRuleCards();
        panel.Children.Add(_rulesListPanel);

        // "Add rule" gives a blank rule to fill in by hand; "Add current network" captures the live
        // adapter's description, MAC and subnet in one step (issue #34 moved it here from the tray). The
        // latter is the better path whenever the user is standing on the network they want a rule for,
        // which is why it sits directly beside the former rather than in a menu somewhere.
        var addButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 2, 0, 0),
        };
        var addBtn = new Button { Content = "Add rule" };
        addBtn.Click += (_, _) => AddRule();
        var addCurrentBtn = new Button { Content = "Add current network" };
        addCurrentBtn.Click += async (_, _) =>
        {
            await _network.AddCurrentAsBridgedAsync();
            if (_closed) return;
            // The rule was written straight to config (not through _workingRules), so the editor above is
            // now a rule short. Re-render from config rather than leaving it stale.
            RefreshValuesFromConfig();
        };
        addButtons.Children.Add(addBtn);
        addButtons.Children.Add(addCurrentBtn);
        panel.Children.Add(addButtons);

        // Fallback (editable) — the switch + target VMs used when no rule matches.
        panel.Children.Add(new TextBlock
        {
            Text = "Fallback", FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 12, 0, 0),
        });

        var fb = _config.Current.Fallback;
        _fallbackSwitchCombo = SuggestionCombo(
            fb.VirtualSwitch,
            "Hyper-V switch name",
            snapshot => snapshot.SwitchNames,
            _ => CommitFallback());
        panel.Children.Add(SettingRow(
            "Fallback switch",
            "Used when no rule matches (typically a NAT switch such as the Hyper-V \"Default Switch\"). "
            + "Offers the host's switches; any name can still be typed.",
            _fallbackSwitchCombo));

        // One VM per line (fix 8): unambiguous even when a VM name contains a comma.
        _fallbackVmsBox = new TextBox
        {
            Text         = SettingsOptions.JoinVmLines(fb.TargetVms),
            MinWidth     = 220,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
        };
        _fallbackVmsBox.LostFocus += (_, _) => CommitFallback();
        panel.Children.Add(SettingRow(
            "Fallback target VMs",
            "VM names reconnected to the fallback switch — one per line. Add one from the host's VMs, "
            + "or type a name (a VM that does not exist yet is allowed).",
            WithVmPicker(_fallbackVmsBox, CommitFallback)));

        panel.Children.Add(new TextBlock
        {
            Text = "Override", FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 12, 0, 0),
        });
        panel.Children.Add(BuildOverrideRow());

        return panel;
    }

    /// <summary>
    /// "Override VM switch" — the tray's quick command, also reachable here so the tray item is a
    /// convenience copy rather than the sole home (issue #34's policy: Settings is the superset).
    ///
    /// <para>Not a config editor: this fires a live, TRANSIENT action — the next network change
    /// re-evaluates the rules and reverts it. The label says so before the click, and the balloon
    /// <see cref="NetworkActions.OverrideSwitchAsync"/> raises says so again after (issue #37). The
    /// controls deliberately carry no commit handlers: nothing here writes config, so merely opening
    /// this section — or tabbing through it — must not touch the network.</para>
    /// </summary>
    private UIElement BuildOverrideRow()
    {
        var vmCombo     = new ComboBox { MinWidth = 160, PlaceholderText = "VM" };
        var switchCombo = new ComboBox { MinWidth = 160, PlaceholderText = "Virtual switch" };

        // Both lists come from config (the managed VMs, and the switches any rule or the fallback names),
        // NOT from the host — an override only makes sense for a VM this app manages onto a switch it
        // knows about, which is exactly the tray submenu's pairing. VmConfigUi.OverrideSwitchNames is
        // shared with the tray so the two surfaces can't offer different sets.
        var vmNames  = _config.Current.VirtualMachines.Select(v => v.Name).ToList();
        var switches = VmConfigUi.OverrideSwitchNames(
            _config.Current.Fallback.VirtualSwitch,
            _config.Current.Rules.Select(r => r.VirtualSwitch));

        WithUpdatingSuppressed(() =>
        {
            foreach (var n in vmNames)  vmCombo.Items.Add(n);
            foreach (var s in switches) switchCombo.Items.Add(s);
            if (vmNames.Count  > 0) vmCombo.SelectedIndex     = 0;
            if (switches.Count > 0) switchCombo.SelectedIndex = 0;
        });

        var applyBtn = new Button
        {
            Content   = "Apply override",
            IsEnabled = vmNames.Count > 0 && switches.Count > 0,
        };
        applyBtn.Click += async (_, _) =>
        {
            if (vmCombo.SelectedItem is not string vm || switchCombo.SelectedItem is not string sw) return;
            UiActivityLog.Logger.LogInformation("Settings: Override switch '{Vm}' → '{Switch}'", vm, sw);
            await _network.OverrideSwitchAsync(vm, sw);
        };

        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
        };
        controls.Children.Add(vmCombo);
        controls.Children.Add(switchCombo);
        controls.Children.Add(applyBtn);

        return SettingRow(
            "Override VM switch",
            vmNames.Count == 0
                ? "No VMs are managed yet — add one under Managed VMs first."
                : "Force a managed VM onto a specific virtual switch now. This is temporary: the next "
                  + "network change re-evaluates the rules and reverts it.",
            controls);
    }

    /// <summary>
    /// Pairs a one-VM-per-line box with an "Add VM" picker fed by the host's discovered VMs (issue #41).
    ///
    /// <para>The box is kept — it round-trips a name containing a comma safely (fix 8) and is what makes
    /// a not-yet-created VM expressible. The picker only APPENDS a line
    /// (<see cref="SettingsOptions.AppendVmLine"/>), so it can neither replace what the user typed nor
    /// duplicate a VM already listed.</para>
    /// </summary>
    private FrameworkElement WithVmPicker(TextBox vmsBox, Action commit)
    {
        var picker = PickerButton(
            "Add VM",
            "No VMs found on this host",
            snapshot => [.. snapshot.VmNames.Select(v => (v, v))],
            vmName =>
            {
                vmsBox.Text = SettingsOptions.AppendVmLine(vmsBox.Text, vmName);
                commit();
            });

        var stack = new StackPanel { Spacing = 6, MinWidth = 220 };
        stack.Children.Add(vmsBox);
        stack.Children.Add(picker);
        return stack;
    }

    /// <summary>
    /// Rebuilds every rule card. Called both during a full <see cref="BuildSections"/> and on its own by
    /// Add/Remove rule, so it owns the rule cards' picker registrations (issue #41): the previous cards'
    /// callbacks are dropped — they capture controls this method is detaching — and the fresh ones are
    /// populated from the cached snapshot, so a rule added after the host was read gets its switch and VM
    /// suggestions immediately rather than only after the next enumeration.
    /// </summary>
    private void RebuildRuleCards()
    {
        if (_rulesListPanel is null) return;
        _rulesListPanel.Children.Clear();

        _ruleConsumers.Clear();
        if (_workingRules.Count == 0)
        {
            _rulesListPanel.Children.Add(Card(new TextBlock
            {
                Text = "No rules yet. Add one, or edit config.json directly.", Opacity = 0.75, TextWrapping = TextWrapping.Wrap,
            }));
            return;
        }

        // Route the cards' registrations into the rule-scoped list, then always restore the sink — a
        // section built after this one (or a later Add) must not land its callbacks in a list that
        // RebuildRuleCards will clear out from under it.
        _consumerSink = _ruleConsumers;
        try
        {
            foreach (var rule in _workingRules)
                _rulesListPanel.Children.Add(BuildRuleCard(rule));
        }
        finally { _consumerSink = _sectionConsumers; }

        if (_inventory is not null) ApplyInventory(_inventory);
    }

    private void AddRule()
    {
        _workingRules.Add(new NetworkRule { Name = "New rule", Priority = 100, VirtualSwitch = "" });
        RebuildRuleCards();
        CommitRules();
    }

    private UIElement BuildRuleCard(NetworkRule rule)
    {
        var grid = new Grid { RowSpacing = 8, ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 7; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        int row = 0;

        // Places an already-built control in the label/field grid. Split out of TextField so the fields
        // that are no longer a bare TextBox (issue #41: the switch picker, and the boxes that gained a
        // "pick from the host" affordance) sit on exactly the same grid rows as the ones that still are.
        void Field(string label, FrameworkElement control)
        {
            var lbl = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Opacity = 0.8 };
            Grid.SetRow(lbl, row); Grid.SetColumn(lbl, 0);
            Grid.SetRow(control, row); Grid.SetColumn(control, 1);
            grid.Children.Add(lbl); grid.Children.Add(control);
            row++;
        }

        TextBox TextField(string label, string? value, string? placeholder, Action<string> commit, Func<string, bool>? validate = null, bool multiline = false, Func<TextBox, FrameworkElement>? wrap = null)
        {
            var box = new TextBox { Text = value ?? "", PlaceholderText = placeholder ?? "", HorizontalAlignment = HorizontalAlignment.Stretch };
            if (multiline) { box.AcceptsReturn = true; box.TextWrapping = TextWrapping.Wrap; }
            box.LostFocus += (_, _) =>
            {
                if (_updating) return;
                if (validate is not null && !validate(box.Text))
                {
                    // Invalid — flag it and don't persist a malformed value.
                    box.BorderBrush     = Brush("SystemFillColorCriticalBrush");
                    box.BorderThickness = new Thickness(1);
                    return;
                }
                box.ClearValue(Control.BorderBrushProperty);
                box.ClearValue(Control.BorderThicknessProperty);
                commit(box.Text);
                CommitRules();
            };
            Field(label, wrap is null ? box : wrap(box));
            return box;
        }

        // Header row: name + remove button.
        var nameBox = new TextBox { Text = rule.Name, PlaceholderText = "Rule name", HorizontalAlignment = HorizontalAlignment.Stretch };
        nameBox.LostFocus += (_, _) => { if (_updating) return; rule.Name = nameBox.Text.Trim(); CommitRules(); };
        var removeBtn = new Button { Content = "Remove" };
        removeBtn.Click += (_, _) => { _workingRules.Remove(rule); RebuildRuleCards(); CommitRules(); };
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(nameBox, 0); Grid.SetColumn(removeBtn, 1);
        header.Children.Add(nameBox); header.Children.Add(removeBtn);
        Grid.SetRow(header, row); Grid.SetColumn(header, 0); Grid.SetColumnSpan(header, 2);
        grid.Children.Add(header);
        row++;

        // Priority (NumberBox).
        var prioLabel = new TextBlock { Text = "Priority", VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Opacity = 0.8 };
        Grid.SetRow(prioLabel, row); Grid.SetColumn(prioLabel, 0);
        var prioBox = new NumberBox
        {
            Value = rule.Priority, Minimum = 0, Maximum = 100_000, SmallChange = 1, LargeChange = 10,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 140,
        };
        prioBox.ValueChanged += (_, _) =>
        {
            if (_updating) return;
            rule.Priority = SettingsOptions.NormalizePriority(double.IsNaN(prioBox.Value) ? 0 : (int)prioBox.Value);
            CommitRules();
        };
        Grid.SetRow(prioBox, row); Grid.SetColumn(prioBox, 1);
        grid.Children.Add(prioLabel); grid.Children.Add(prioBox);
        row++;

        // Adapter MAC keeps its TextBox and its EXACT validation gating (an invalid MAC is flagged and not
        // persisted) — issue #41 only adds a way to fill it from a real adapter instead of transcribing a
        // MAC by hand, which is the field most likely to be mistyped and the hardest to eyeball.
        TextField("Adapter MAC", rule.Conditions.AdapterMac, "AA:BB:CC:DD:EE:FF (optional)",
            v => rule.Conditions.AdapterMac = SettingsOptions.CanonicalizeMac(v), SettingsOptions.IsValidMac,
            wrap: box => WithMacPicker(box, mac =>
            {
                rule.Conditions.AdapterMac = SettingsOptions.CanonicalizeMac(mac);
                CommitRules();
            }));
        TextField("IP subnet (CIDR)", rule.Conditions.IpCidr, "10.0.0.0/23 (optional)",
            v => rule.Conditions.IpCidr = SettingsOptions.BlankToNull(v), SettingsOptions.IsValidCidr);

        // The switch a typo here silently costs everything: the rule matches the network, then binds
        // nothing, and says so only in a log line (issue #17's failure). Suggest the host's real switches.
        Field("Virtual switch", SuggestionCombo(
            rule.VirtualSwitch,
            "Hyper-V switch name",
            snapshot => snapshot.SwitchNames,
            v => { rule.VirtualSwitch = v.Trim(); CommitRules(); }));

        // One VM per line (not comma-separated) so a VM whose name contains a comma round-trips intact (fix 8).
        TextField("Target VMs", SettingsOptions.JoinVmLines(rule.TargetVms), "One VM name per line",
            v => rule.TargetVms = SettingsOptions.ParseVmLines(v), multiline: true,
            wrap: box => WithVmPicker(box, () =>
            {
                rule.TargetVms = SettingsOptions.ParseVmLines(box.Text);
                CommitRules();
            }));

        // Auto-start toggle.
        var autoLabel = new TextBlock { Text = "Auto-start VMs", VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Opacity = 0.8 };
        Grid.SetRow(autoLabel, row); Grid.SetColumn(autoLabel, 0);
        var autoToggle = new ToggleSwitch { IsOn = rule.AutoStart, OnContent = "On", OffContent = "Off", HorizontalAlignment = HorizontalAlignment.Left };
        autoToggle.Toggled += (_, _) => { if (_updating) return; rule.AutoStart = autoToggle.IsOn; CommitRules(); };
        Grid.SetRow(autoToggle, row); Grid.SetColumn(autoToggle, 1);
        grid.Children.Add(autoLabel); grid.Children.Add(autoToggle);

        return Card(grid);
    }

    private void CommitRules()
    {
        if (_updating) return;
        // Snapshot the working list so the background write sees a stable copy.
        var snapshot = _workingRules.Select(ConfigManager.CleanRule).ToList();
        Task.Run(() =>
        {
            try { _config.SaveRules(snapshot); }
            catch (Exception ex)
            {
                _ui.TryEnqueue(() =>
                {
                    if (_closed) return;
                    NativeMethods.Warn($"Could not save the network rules:\n\n{ex.Message}", AppInfo.Name);
                });
            }
        });
    }

    /// <summary>
    /// Pairs the MAC box with a picker listing the host's physical adapters (issue #41), writing the
    /// chosen adapter's canonical MAC into the box. Adapters whose MAC is unknown are not offered —
    /// <see cref="PhysicalAdapterInfo.Mac"/> is "—" in that case, and filling the field with a dash would
    /// be worse than the retyping this replaces.
    ///
    /// <para>A picked MAC is by construction well-formed, so the invalid-value border the box may be
    /// wearing is cleared — the field is now valid and must not keep saying otherwise.</para>
    /// </summary>
    private FrameworkElement WithMacPicker(TextBox macBox, Action<string> commit)
    {
        var picker = PickerButton(
            "Pick adapter",
            "No adapters found on this host",
            snapshot =>
            [
                .. snapshot.Adapters
                    .Where(a => SettingsOptions.IsValidMac(a.Mac) && !string.IsNullOrWhiteSpace(a.Mac) && a.Mac != "—")
                    .Select(a => ($"{a.DisplayName} — {a.Mac}", a.Mac))
            ],
            mac =>
            {
                macBox.Text = SettingsOptions.CanonicalizeMac(mac) ?? mac;
                macBox.ClearValue(Control.BorderBrushProperty);
                macBox.ClearValue(Control.BorderThicknessProperty);
                commit(macBox.Text);
            });

        var stack = new StackPanel { Spacing = 6, MinWidth = 220 };
        stack.Children.Add(macBox);
        stack.Children.Add(picker);
        return stack;
    }

    private void CommitFallback()
    {
        if (_updating || _fallbackSwitchCombo is null || _fallbackVmsBox is null) return;
        var sw   = _fallbackSwitchCombo.Text;
        var vms  = SettingsOptions.ParseVmLines(_fallbackVmsBox.Text);
        Task.Run(() =>
        {
            try { _config.SetFallback(sw, vms); }
            catch (Exception ex)
            {
                _ui.TryEnqueue(() =>
                {
                    if (_closed) return;
                    NativeMethods.Warn($"Could not save the fallback switch:\n\n{ex.Message}", AppInfo.Name);
                });
            }
        });
    }

    // ── Adapters (rename) ─────────────────────────────────────────────────────────

    private UIElement BuildAdaptersSection()
    {
        var panel = Section("Adapters");
        // Worded to match RenameAdapterWindow's own explanation of the same act (issue #42), and saying
        // "adapters" where it used to say "NICs". The two "Hyper-V Manager" mentions here and in that
        // dialog DO mean Microsoft's MMC snap-in — one of the places the description surfaces — so they
        // stay; it is this app's own popup title that must never claim that name.
        panel.Children.Add(Description(
            "Rename a physical adapter's description — the text Device Manager, Hyper-V Manager and " +
            "Windows Settings show for it. It does not change the adapter's Windows name (its connection " +
            "alias). Renaming briefly drops that adapter's connection. Only real physical adapters are " +
            "listed — Bluetooth/VPN/virtual adapters are hidden."));

        // Adapter enumeration (AdapterMatcher.GetPhysicalAdapters) hits WMI/NDIS and can stall on a
        // flaky USB-Ethernet dock. Never do it on the UI thread inside the ctor (that would freeze the
        // Settings window open — the "nothing happens" symptom). Show a placeholder now and fill the
        // rows in from a background thread when ready.
        //
        // The enumeration itself now arrives with the rest of the host inventory (issue #41), so the
        // window makes ONE adapter read that both this section and the rules editor's MAC picker consume,
        // rather than one per feature. Its own container panel, refilled wholesale on each snapshot, so a
        // second enumeration (a config reload re-reads the host) refreshes these rows rather than being
        // dropped on the floor by a stale index.
        var rowsPanel = new StackPanel { Spacing = 8 };
        rowsPanel.Children.Add(Card(new TextBlock { Text = "Loading adapters…", Opacity = 0.75 }));
        panel.Children.Add(rowsPanel);

        _consumerSink.Add(snapshot =>
        {
            rowsPanel.Children.Clear();
            foreach (var r in BuildAdapterRows(snapshot.Adapters)) rowsPanel.Children.Add(r);
        });

        return panel;
    }

    private List<UIElement> BuildAdapterRows(IReadOnlyList<PhysicalAdapterInfo> adapters)
    {
        if (adapters.Count == 0)
            return [Card(new TextBlock { Text = "No renameable network adapters found.", Opacity = 0.75 })];

        var rows = new List<UIElement>(adapters.Count);
        foreach (var a in adapters)
        {
            var adapter = a;   // capture per iteration
            // DisplayName, not Description: the rename writes the device FriendlyName, which is what
            // DisplayName carries — Description would show the unchanged factory string (issue #32).
            var label = string.IsNullOrWhiteSpace(a.InterfaceAlias) || a.InterfaceAlias == a.DisplayName
                ? a.DisplayName
                : $"{a.InterfaceAlias} — {a.DisplayName}";

            var renameBtn = new Button { Content = "Rename…" };
            renameBtn.Click += (_, _) => _ = _renameFlow.RunAsync(adapter);
            rows.Add(SettingRow(label, adapter.Mac, renameBtn));
        }
        return rows;
    }

    // ── Maintenance ────────────────────────────────────────────────────────────────

    // The last "Reload config from disk" confirmation, held here rather than on the TextBlock because a
    // successful reload rebuilds the whole Maintenance section and would otherwise discard it (issue #39).
    // Null = nothing to show (never reloaded, or the last reload failed and said so in a dialog instead).
    private string? _reloadResultMessage;

    private UIElement BuildMaintenanceSection()
    {
        var panel = Section("Maintenance");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        var openConfig = new Button { Content = "Open config.json" };
        openConfig.Click += (_, _) => Shell.OpenOrReveal(ConfigManager.GetConfigPath());

        // The app now writes three separate logs (switcher.log, vm-power.log, ui.log — issues #20/#21),
        // so a single "Open log file" button no longer covers them. Expose each one, plus a reveal of
        // the whole data folder (which also surfaces crash.log).
        var openLog = new DropDownButton { Content = "Open log" };
        var logMenu = new MenuFlyout();
        void AddLogItem(string text, string path) =>
            logMenu.Items.Add(new MenuFlyoutItem
            {
                Text = text,
                Command = new RelayCommand(() => Shell.OpenOrReveal(path)),
            });
        AddLogItem("Switcher log", AppInfo.LogFile);
        AddLogItem("VM power log", AppInfo.VmPowerLog);
        AddLogItem("UI log", AppInfo.UiLog);
        logMenu.Items.Add(new MenuFlyoutSeparator());
        logMenu.Items.Add(new MenuFlyoutItem
        {
            Text = "Open logs folder",
            Command = new RelayCommand(() => Shell.OpenOrReveal(AppInfo.DataDir)),
        });
        openLog.Flyout = logMenu;

        // Inline outcome for the happy path (issue #39). A modal for "it worked" is noise; silence,
        // however, was worse — the button gave no signal at all, so a reload that parsed and one that
        // threw looked identical from here.
        var reloadResult = new TextBlock
        {
            TextWrapping      = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity           = 0.75,
            // A successful reload rebuilds every section — including this one — so the confirmation is
            // rendered from the field rather than written onto this instance, which by then is detached.
            Text       = _reloadResultMessage ?? string.Empty,
            Visibility = _reloadResultMessage is null ? Visibility.Collapsed : Visibility.Visible,
        };

        var reload = new Button { Content = "Reload config from disk" };
        reload.Click += (_, _) => Task.Run(() =>
        {
            var outcome = _config.Load();
            _ui.TryEnqueue(() =>
            {
                if (_closed) return;

                // THE point of this issue: only re-render the window from ConfigManager.Current when the
                // load actually succeeded. On a parse failure Current still holds the PREVIOUS config, so
                // the old code's unconditional RefreshValuesFromConfig re-drew the user's stale values and
                // presented them as what had just been read off disk — a surface asserting a state the app
                // never confirmed. ShouldRebuildFromConfig is the gate; the dialog is the honest answer.
                if (!ConfigLoadUi.ShouldRebuildFromConfig(outcome))
                {
                    // No rebuild happened, so this TextBlock is still the live one — clear it directly.
                    // Leaving a previous "Reloaded — 3 rules, 2 VMs" on screen next to a failed reload
                    // would be the same lie in a smaller font.
                    _reloadResultMessage    = null;
                    reloadResult.Text       = string.Empty;
                    reloadResult.Visibility = Visibility.Collapsed;
                    NativeMethods.Error(ConfigLoadUi.FailureMessage(outcome)!, AppInfo.Name);
                    return;
                }

                _reloadResultMessage = ConfigLoadUi.SuccessMessage(outcome);
                RefreshValuesFromConfig();   // rebuilds this section, which renders the message above
            });
        });

        buttons.Children.Add(openConfig);
        buttons.Children.Add(openLog);
        buttons.Children.Add(reload);
        buttons.Children.Add(reloadResult);

        panel.Children.Add(SettingRow(
            "Config & logs",
            "Open the raw config.json, open any of the app's log files (switcher, VM power, UI) or the logs folder, "
            + "or re-read config.json from disk after an out-of-band edit. A reload that can't parse the file says "
            + "so and changes nothing — the settings already loaded stay active.",
            buttons));

        // ── The network tools (issue #34) ──
        // "Re-check network now" is ALSO a tray quick command — a convenience copy there, per the policy
        // that this window is the complete surface. "Repair host networking" is here ONLY.
        var networkButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        var recheckBtn = new Button { Content = "Re-check network now" };
        recheckBtn.Click += (_, _) => _ = _network.ReCheckNetworkAsync();

        var repairBtn = new Button { Content = "Repair host networking" };
        repairBtn.Click += (_, _) => _ = _network.RepairHostNetworkingAsync();

        networkButtons.Children.Add(recheckBtn);
        networkButtons.Children.Add(repairBtn);

        panel.Children.Add(SettingRow(
            "Network",
            // The recorded trade-off of moving Repair off the tray (issue #34): the automatic pass runs
            // once, ~15 s after startup, so a mid-session dock cycle needs this button. Naming the exact
            // symptom is what makes it findable at the moment it is needed — which is the moment the
            // user's wired connection has just vanished.
            "Re-evaluate the rules against the current network and report the result, or repair the host's "
            + "own networking when the host is offline but the VM is online (a duplicate host adapter left "
            + "behind by a dock cycle). Neither changes any setting.",
            networkButtons));

        var updateBtn = new Button { Content = "Check for updates" };
        updateBtn.Click += (_, _) => _ = CheckForUpdatesAsync();
        panel.Children.Add(SettingRow(
            "Updates",
            "Check GitHub for a newer release.",
            updateBtn));

        return panel;
    }

    private async Task CheckForUpdatesAsync()
    {
        // Parent the dialog to this window so it isn't orphaned. UpdatePrompt.RunAsync must stay on
        // the UI thread (comctl32 v6 activation context for the Task Dialog).
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        try { await UpdatePrompt.RunAsync(_updateChecker, hwnd); }
        catch (Exception ex) { AppInfo.AppendCrashLogLine("SettingsWindow", $"CheckForUpdates: {ex}"); }
    }

    // ── About ──────────────────────────────────────────────────────────────────────

    private UIElement BuildAboutSection()
    {
        var panel = Section("About");

        // Embed the shared About *content* inline rather than a button that opens a second window.
        // BrandAboutControl (issue #19) is a hostable UserControl built for exactly this — it renders
        // the brand header, description, the three link buttons and the external-libraries credits, and
        // owns no window chrome. Wrapping it in the standard Card sets it apart as its own panel without
        // being a separate dialog. The About window's one extra affordance — "Check for updates" — isn't
        // lost: it already has its own row in the Maintenance → Updates card above.
        var about = new BrandAboutControl
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth            = 520,
        };
        about.SetInfo(AppAbout.CreateInfo());
        panel.Children.Add(Card(about));

        return panel;
    }

    /// <summary>
    /// Re-syncs the whole window to the freshly reloaded <see cref="ConfigManager.Current"/> after an
    /// explicit "Reload config from disk" (issue #24 — previously only the log level was re-read, leaving
    /// VM rows, network rules and the fallback showing stale values). A full section rebuild is the robust
    /// way to pick up VMs/rules added or removed by an out-of-band edit; every populate path is already
    /// re-entrancy-guarded (<see cref="WithUpdatingSuppressed"/>) so the rebuild itself commits nothing.
    /// The visible category is preserved (panel visibility is owned by the nav, which BuildSections leaves
    /// untouched). _userToggledStartup is reset so the fresh startup toggle re-reads the real task state.
    /// </summary>
    private void RefreshValuesFromConfig()
    {
        if (_closed) return;
        _userToggledStartup = false;
        try { BuildSections(); }
        catch (Exception ex) { AppInfo.AppendCrashLogLine("SettingsWindow", $"RefreshValuesFromConfig: {ex}"); }
    }

    // ── Small view helpers (shared card idiom, matching DashboardWindow) ──────────

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>
    /// Fills a ComboBox with one <see cref="ComboBoxItem"/> per option label and selects
    /// <paramref name="selectedIndex"/>. Shared by the log-level and per-VM action pickers so their
    /// populate logic can't drift. (The delay picker uses <see cref="LoadDelayCombo"/> — its items
    /// carry an int Tag and support a custom entry, so it isn't a fit for this.)
    /// </summary>
    private static void PopulateLabelCombo<T>(ComboBox combo, IReadOnlyList<(string Label, T Value)> options, int selectedIndex)
    {
        combo.Items.Clear();
        foreach (var (label, _) in options)
            combo.Items.Add(new ComboBoxItem { Content = label });
        combo.SelectedIndex = selectedIndex;
    }

    private static StackPanel Section(string title)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text       = title,
            FontSize   = 20,
            FontWeight = FontWeights.SemiBold,
            Margin     = new Thickness(0, 0, 0, 4),
        });
        return panel;
    }

    private static TextBlock Description(string text) => new()
    {
        Text         = text,
        FontSize     = 12,
        Opacity      = 0.75,
        TextWrapping = TextWrapping.Wrap,
        Margin       = new Thickness(2, 0, 0, 2),
    };

    private static Border Card(UIElement child) => new()
    {
        CornerRadius    = new CornerRadius(6),
        Padding         = new Thickness(14, 12, 14, 12),
        Background      = Brush("CardBackgroundFillColorDefaultBrush"),
        BorderBrush     = Brush("CardStrokeColorDefaultBrush"),
        BorderThickness = new Thickness(1),
        Child           = child,
    };

    /// <summary>
    /// A settings card: header (+ optional description) on the left, a control on the right — dropping
    /// the control beneath the text on a row too narrow for both (issue #31; see
    /// <see cref="SettingRowPanel"/>, which owns that decision).  Child order is load-bearing: the panel
    /// arranges <c>Children[0]</c> as the text and <c>Children[1]</c> as the control.
    /// </summary>
    private static Border SettingRow(string header, string? description, FrameworkElement control) =>
        Card(Row(header, description, control));

    /// <summary>
    /// The row itself, without the surrounding card — so several settings can share one card while each
    /// keeps the responsive text/control layout (see <see cref="BuildVmCard"/>).  Child order is
    /// load-bearing: <see cref="SettingRowPanel"/> arranges <c>Children[0]</c> as the text and
    /// <c>Children[1]</c> as the control.
    /// </summary>
    private static SettingRowPanel Row(string header, string? description, FrameworkElement control)
    {
        var left = new StackPanel { Spacing = 2 };
        left.Children.Add(new TextBlock
        {
            Text         = header,
            FontSize     = 13,
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrEmpty(description))
            left.Children.Add(new TextBlock
            {
                Text         = description,
                FontSize     = 11,
                Opacity      = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });

        var row = new SettingRowPanel();
        row.Children.Add(left);
        row.Children.Add(control);
        return row;
    }

    /// <summary>
    /// Shows a warning from a background save, marshalled to the UI thread and dropped if the window has
    /// since closed — the guard every one of these call sites needs and previously repeated inline.
    /// </summary>
    private void WarnOnUi(string message) => _ui.TryEnqueue(() =>
    {
        if (_closed) return;
        NativeMethods.Warn(message, AppInfo.Name);
    });

    private void WithUpdatingSuppressed(Action apply)
    {
        _updating = true;
        try { apply(); }
        finally { _updating = false; }
    }
}
