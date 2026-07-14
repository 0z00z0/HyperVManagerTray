using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using HyperVManagerTray.Helpers;
using HyperVManagerTray.Services;

namespace HyperVManagerTray.UI;

/// <summary>
/// The consolidated Settings window (issue #18) — pulls configuration that used to be scattered across
/// the tray context menu (and, for a few values, only reachable by hand-editing config.json) into one
/// grouped, titled window: General (run-on-startup, log level), Managed VMs (per-VM on-bridge-lost
/// action + delay), Network adapters (rename a physical NIC's description; fallback-switch info), and
/// Files &amp; maintenance (open config/log, reload, check for updates).
///
/// <para>Everything persists through the existing <see cref="ConfigManager"/> (no parallel store).
/// Sections are built in code — the same code-first card idiom <see cref="DashboardWindow"/> uses — so
/// the app keeps one visual language without a separate settings-control dependency.</para>
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
    // First-open size (DIPs, scaled to the monitor and capped to its work area). A comfortable
    // reading width for single-column settings cards — never oversized on a large/ultrawide screen.
    private const int DefaultWidth  = 640;
    private const int DefaultHeight = 720;

    private readonly ConfigManager    _config;
    private readonly StartupManager   _startup;
    private readonly UpdateChecker    _updateChecker;
    private readonly AdapterRenameFlow _renameFlow;
    private readonly DispatcherQueue  _ui;

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

    public SettingsWindow(ConfigManager config, StartupManager startup, UpdateChecker updateChecker)
    {
        _config        = config;
        _startup       = startup;
        _updateChecker = updateChecker;

        InitializeComponent();
        Title = $"{AppInfo.Name} — Settings";

        _ui = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("SettingsWindow must be created on the UI thread.");
        _renameFlow = new AdapterRenameFlow(_config, _ui);

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
    private void ShowLoadFailurePlaceholder() => RootPanel.Children.Add(new TextBlock
    {
        Text         = "Settings could not be fully loaded. See the crash log for details.",
        TextWrapping = TextWrapping.Wrap,
        Margin       = new Thickness(0, 8, 0, 0),
    });

    private void OnClosed(object sender, WindowEventArgs e)
    {
        _closed = true;
        if (_startupToggle is not null) _startupToggle.Toggled         -= OnStartupToggled;
        if (_logLevelCombo is not null) _logLevelCombo.SelectionChanged -= OnLogLevelChanged;
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
        AppWindow.SetPresenter(presenter);

        // Native, single-monitor placement — the same MonitorFromPoint path every other window in
        // this app uses. Centre on the monitor under the cursor, capped to its work area so it is
        // never larger than the screen. No DisplayArea.FindAll (see the class remarks).
        var (work, scale) = NativeMethods.GetCursorMonitorMetrics();
        int workW = work.Right  - work.Left;
        int workH = work.Bottom - work.Top;
        int w = Math.Min((int)Math.Round(DefaultWidth  * scale), workW);
        int h = Math.Min((int)Math.Round(DefaultHeight * scale), workH);

        AppWindow.Resize(new SizeInt32(w, h));
        AppWindow.Move(new PointInt32(work.Left + (workW - w) / 2, work.Top + (workH - h) / 2));
    }

    // ── Section composition ──────────────────────────────────────────────────────

    private void BuildSections()
    {
        RootPanel.Children.Clear();
        RootPanel.Children.Add(BuildGeneralSection());
        RootPanel.Children.Add(BuildVmSection());
        RootPanel.Children.Add(BuildNetworkSection());
        RootPanel.Children.Add(BuildMaintenanceSection());
        RootPanel.Children.Add(new TextBlock
        {
            Text       = $"{AppInfo.Name}  ·  v{AppInfo.Version}",
            FontSize   = 11,
            Opacity    = 0.6,
            Margin     = new Thickness(2, 4, 0, 0),
        });
    }

    // ── General ──────────────────────────────────────────────────────────────────

    private UIElement BuildGeneralSection()
    {
        var panel = Section("General");

        // Run on startup — the tray toggle moved here. IsEnabled reflects the scheduled task, read
        // off the UI thread (schtasks can take up to a couple of seconds).
        _startupToggle = new ToggleSwitch { OnContent = "On", OffContent = "Off" };
        _startupToggle.Toggled += OnStartupToggled;
        panel.Children.Add(SettingRow(
            "Run on startup",
            "Start automatically at sign-in (elevated scheduled task). Off until the status loads.",
            _startupToggle));
        LoadStartupStateAsync();

        // Log level — previously only editable by hand in config.json.
        _logLevelCombo = new ComboBox { MinWidth = 200 };
        WithUpdatingSuppressed(() => PopulateLabelCombo(
            _logLevelCombo, SettingsOptions.LogLevels, SettingsOptions.LogLevelToIndex(_config.Current.LogLevel)));
        _logLevelCombo.SelectionChanged += OnLogLevelChanged;
        panel.Children.Add(SettingRow(
            "Log level",
            "Minimum severity written to switcher.log. Debug captures diagnostic detail; None disables logging.",
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
            "What to do with each managed VM when the bridged network is lost (the switch falls back " +
            "to the default). The action is cancelled if the bridge returns within the delay."));

        var vms = _config.Current.VirtualMachines;
        if (vms.Count == 0)
        {
            panel.Children.Add(Card(new TextBlock
            {
                Text         = "No VMs are managed yet. Add one from the tray's VM Power menu, then it will appear here.",
                TextWrapping = TextWrapping.Wrap,
                Opacity      = 0.75,
            }));
            return panel;
        }

        foreach (var vm in vms)
            panel.Children.Add(BuildVmRow(vm.Name, vm.OnBridgeLostAction, vm.OnBridgeLostDelaySeconds));

        return panel;
    }

    private UIElement BuildVmRow(string vmName, string? action, int delaySeconds)
    {
        var actionCombo = new ComboBox { MinWidth = 150 };
        var delayCombo  = new ComboBox { MinWidth = 130 };

        WithUpdatingSuppressed(() =>
        {
            PopulateLabelCombo(actionCombo, SettingsOptions.BridgeLostActions,
                SettingsOptions.BridgeLostActionToIndex(action));

            LoadDelayCombo(delayCombo, SettingsOptions.NormalizeDelaySeconds(delaySeconds));
            delayCombo.IsEnabled = SettingsOptions.NormalizeBridgeLostAction(action) is not null;
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
                catch (Exception ex)
                {
                    _ui.TryEnqueue(() =>
                    {
                        if (_closed) return;
                        NativeMethods.Warn(
                            $"Could not save the setting for {vmName}:\n\n{ex.Message}", AppInfo.Name);
                    });
                }
            });
        }

        actionCombo.SelectionChanged += (_, _) => Commit();
        delayCombo.SelectionChanged  += (_, _) => Commit();

        // Two controls on the right, stacked label above them so the row stays readable.
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        controls.Children.Add(actionCombo);
        controls.Children.Add(delayCombo);

        return SettingRow(vmName, "Action · delay when the bridge is lost", controls);
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

    // ── Network adapters ─────────────────────────────────────────────────────────

    private UIElement BuildNetworkSection()
    {
        var panel = Section("Network adapters");
        panel.Children.Add(Description(
            "Rename a physical network adapter's Windows description (Device Manager, Hyper-V Manager, " +
            "Settings). Renaming briefly drops that adapter's connection."));

        // Adapter enumeration (AdapterMatcher.GetPhysicalAdapters) hits WMI/NDIS and can stall on a
        // flaky USB-Ethernet dock. Never do it on the UI thread inside the ctor (that would freeze the
        // Settings window open — the "nothing happens" symptom). Show a placeholder now and fill the
        // rows in from a background thread when ready.
        var loading = Card(new TextBlock { Text = "Loading adapters…", Opacity = 0.75 });
        panel.Children.Add(loading);
        LoadAdaptersAsync(panel, loading);

        // Fallback-switch info (read-only — editing switch names freely could break binding; this
        // just surfaces what config.json currently uses when no rule matches). Cheap: read from config.
        var fb = _config.Current.Fallback;
        var fbTargets = fb.TargetVms.Count > 0 ? string.Join(", ", fb.TargetVms) : "none";
        panel.Children.Add(SettingRow(
            "Fallback switch",
            $"Used when no rule matches. Target VMs: {fbTargets}. Edit rules in config.json.",
            new TextBlock
            {
                Text              = fb.VirtualSwitch,
                Foreground        = Brush("TextFillColorSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping      = TextWrapping.Wrap,
            }));

        return panel;
    }

    /// <summary>
    /// Enumerates physical adapters off the UI thread, then marshals back to replace the
    /// "Loading adapters…" placeholder with the built rows (or a "none found" card). Wrapped so a
    /// enumeration or build failure can neither crash the ctor nor leave the placeholder stuck.
    /// </summary>
    private void LoadAdaptersAsync(StackPanel panel, UIElement placeholder) => Task.Run(() =>
    {
        IReadOnlyList<PhysicalAdapterInfo> adapters;
        try   { adapters = AdapterMatcher.GetPhysicalAdapters(); }
        catch { adapters = []; }

        _ui.TryEnqueue(() =>
        {
            if (_closed) return;
            try
            {
                int idx = panel.Children.IndexOf(placeholder);
                if (idx < 0) return;                 // section rebuilt/removed underneath us
                panel.Children.RemoveAt(idx);

                var rows = BuildAdapterRows(adapters);
                for (int i = 0; i < rows.Count; i++)
                    panel.Children.Insert(idx + i, rows[i]);
            }
            catch (Exception ex)
            {
                AppInfo.AppendCrashLogLine("SettingsWindow", $"LoadAdaptersAsync: {ex}");
            }
        });
    });

    private List<UIElement> BuildAdapterRows(IReadOnlyList<PhysicalAdapterInfo> adapters)
    {
        if (adapters.Count == 0)
            return [Card(new TextBlock { Text = "No network adapters found.", Opacity = 0.75 })];

        var rows = new List<UIElement>(adapters.Count);
        foreach (var a in adapters)
        {
            var adapter = a;   // capture per iteration
            var label = string.IsNullOrWhiteSpace(a.InterfaceAlias) || a.InterfaceAlias == a.Description
                ? a.Description
                : $"{a.InterfaceAlias} — {a.Description}";

            var renameBtn = new Button { Content = "Rename…" };
            renameBtn.Click += (_, _) => _ = _renameFlow.RunAsync(adapter);
            rows.Add(SettingRow(label, adapter.Mac, renameBtn));
        }
        return rows;
    }

    // ── Files & maintenance ──────────────────────────────────────────────────────

    private UIElement BuildMaintenanceSection()
    {
        var panel = Section("Files & maintenance");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        var openConfig = new Button { Content = "Open config.json" };
        openConfig.Click += (_, _) => Shell.OpenOrReveal(ConfigManager.GetConfigPath());

        var openLog = new Button { Content = "Open log file" };
        openLog.Click += (_, _) => Shell.OpenOrReveal(AppInfo.LogFile);

        var reload = new Button { Content = "Reload config from disk" };
        reload.Click += (_, _) => Task.Run(() =>
        {
            _config.Load();
            _ui.TryEnqueue(RefreshValuesFromConfig);
        });

        buttons.Children.Add(openConfig);
        buttons.Children.Add(openLog);
        buttons.Children.Add(reload);

        panel.Children.Add(Card(buttons));

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

    /// <summary>
    /// Re-reads the values that are cheap to re-sync in place (log level) after an explicit reload from
    /// disk. VM rows and adapters keep their built structure — reopen the window to reflect a VM added
    /// or removed by an out-of-band edit.
    /// </summary>
    private void RefreshValuesFromConfig()
    {
        if (_closed || _logLevelCombo is null) return;
        WithUpdatingSuppressed(() =>
            _logLevelCombo.SelectedIndex = SettingsOptions.LogLevelToIndex(_config.Current.LogLevel));
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
            FontSize   = 16,
            FontWeight = FontWeights.SemiBold,
            Margin     = new Thickness(0, 0, 0, 2),
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

    /// <summary>A settings card: header (+ optional description) on the left, a control on the right.</summary>
    private static Border SettingRow(string header, string? description, FrameworkElement control)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
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
        Grid.SetColumn(left, 0);

        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);

        grid.Children.Add(left);
        grid.Children.Add(control);
        return Card(grid);
    }

    private void WithUpdatingSuppressed(Action apply)
    {
        _updating = true;
        try { apply(); }
        finally { _updating = false; }
    }
}
