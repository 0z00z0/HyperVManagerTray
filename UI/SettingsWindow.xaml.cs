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
/// description), Maintenance (open config/log, reload, check for updates), and About (opens the shared
/// <see cref="BrandAboutWindow"/>).
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
    // First-open size (DIPs, scaled to the monitor and capped to its work area). A comfortable
    // reading width for the settings cards — never oversized on a large/ultrawide screen.
    private const int DefaultWidth  = 720;
    private const int DefaultHeight = 760;

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

    // Network rules editor (issue #23). _workingRules is the UI's authoritative copy while the window is
    // open (deep copies of config rules); edits mutate it in place and persist via _config.SaveRules, so
    // a reload-driven re-sort of _config.Current can't reorder controls mid-edit.
    private readonly List<NetworkRule> _workingRules = [];
    private StackPanel? _rulesListPanel;
    private TextBox?    _fallbackSwitchBox;
    private TextBox?    _fallbackVmsBox;

    // The Adapters section panel + its "Loading…" placeholder, kept so a reload can rebuild it.
    private StackPanel? _adaptersSection;

    // Reused shared About window, so repeated clicks re-activate one window instead of stacking copies.
    private BrandAboutWindow? _aboutWindow;

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
    private void ShowLoadFailurePlaceholder() => GeneralPanel.Children.Add(new TextBlock
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

    // Toggles which category panel is visible. Guarded against firing during InitializeComponent (the
    // General item's IsSelected="True" raises this before the content panels' backing fields are set)
    // and against a torn-down window.
    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_closed || GeneralPanel is null) return;                 // panels not built yet / window closed
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag }) return;

        GeneralPanel.Visibility     = tag == "General"     ? Visibility.Visible : Visibility.Collapsed;
        VmsPanel.Visibility         = tag == "Vms"         ? Visibility.Visible : Visibility.Collapsed;
        NetworkPanel.Visibility     = tag == "Network"     ? Visibility.Visible : Visibility.Collapsed;
        AdaptersPanel.Visibility    = tag == "Adapters"    ? Visibility.Visible : Visibility.Collapsed;
        MaintenancePanel.Visibility = tag == "Maintenance" ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility       = tag == "About"       ? Visibility.Visible : Visibility.Collapsed;
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
        VersionText.Text = $"v{AppInfo.Version}";

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

        var addBtn = new Button { Content = "Add rule", Margin = new Thickness(0, 2, 0, 0) };
        addBtn.Click += (_, _) => AddRule();
        panel.Children.Add(addBtn);

        // Fallback (editable) — the switch + target VMs used when no rule matches.
        panel.Children.Add(new TextBlock
        {
            Text = "Fallback", FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 12, 0, 0),
        });

        var fb = _config.Current.Fallback;
        _fallbackSwitchBox = new TextBox { Text = fb.VirtualSwitch, MinWidth = 220 };
        _fallbackSwitchBox.LostFocus += (_, _) => CommitFallback();
        panel.Children.Add(SettingRow(
            "Fallback switch",
            "Used when no rule matches (typically a NAT switch such as the Hyper-V \"Default Switch\").",
            _fallbackSwitchBox));

        _fallbackVmsBox = new TextBox { Text = SettingsOptions.JoinVmList(fb.TargetVms), MinWidth = 220 };
        _fallbackVmsBox.LostFocus += (_, _) => CommitFallback();
        panel.Children.Add(SettingRow(
            "Fallback target VMs",
            "Comma-separated VM names reconnected to the fallback switch.",
            _fallbackVmsBox));

        return panel;
    }

    private void RebuildRuleCards()
    {
        if (_rulesListPanel is null) return;
        _rulesListPanel.Children.Clear();
        if (_workingRules.Count == 0)
        {
            _rulesListPanel.Children.Add(Card(new TextBlock
            {
                Text = "No rules yet. Add one, or edit config.json directly.", Opacity = 0.75, TextWrapping = TextWrapping.Wrap,
            }));
            return;
        }
        foreach (var rule in _workingRules)
            _rulesListPanel.Children.Add(BuildRuleCard(rule));
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

        TextBox TextField(string label, string? value, string? placeholder, Action<string> commit, Func<string, bool>? validate = null)
        {
            var lbl = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Opacity = 0.8 };
            Grid.SetRow(lbl, row); Grid.SetColumn(lbl, 0);
            var box = new TextBox { Text = value ?? "", PlaceholderText = placeholder ?? "", HorizontalAlignment = HorizontalAlignment.Stretch };
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
            Grid.SetRow(box, row); Grid.SetColumn(box, 1);
            grid.Children.Add(lbl); grid.Children.Add(box);
            row++;
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

        TextField("Adapter MAC", rule.Conditions.AdapterMac, "AA:BB:CC:DD:EE:FF (optional)",
            v => rule.Conditions.AdapterMac = SettingsOptions.CanonicalizeMac(v), SettingsOptions.IsValidMac);
        TextField("IP subnet (CIDR)", rule.Conditions.IpCidr, "10.0.0.0/23 (optional)",
            v => rule.Conditions.IpCidr = SettingsOptions.BlankToNull(v), SettingsOptions.IsValidCidr);
        TextField("Virtual switch", rule.VirtualSwitch, "Hyper-V switch name",
            v => rule.VirtualSwitch = v.Trim());
        TextField("Target VMs", SettingsOptions.JoinVmList(rule.TargetVms), "VM1, VM2",
            v => rule.TargetVms = SettingsOptions.ParseVmList(v));

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

    private void CommitFallback()
    {
        if (_updating || _fallbackSwitchBox is null || _fallbackVmsBox is null) return;
        var sw   = _fallbackSwitchBox.Text;
        var vms  = SettingsOptions.ParseVmList(_fallbackVmsBox.Text);
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
        _adaptersSection = panel;
        panel.Children.Add(Description(
            "Rename a physical network adapter's Windows description (Device Manager, Hyper-V Manager, " +
            "Settings). Renaming briefly drops that adapter's connection. Only real physical NICs are " +
            "listed — Bluetooth/VPN/virtual adapters are hidden."));

        // Adapter enumeration (AdapterMatcher.GetPhysicalAdapters) hits WMI/NDIS and can stall on a
        // flaky USB-Ethernet dock. Never do it on the UI thread inside the ctor (that would freeze the
        // Settings window open — the "nothing happens" symptom). Show a placeholder now and fill the
        // rows in from a background thread when ready.
        var loading = Card(new TextBlock { Text = "Loading adapters…", Opacity = 0.75 });
        panel.Children.Add(loading);
        LoadAdaptersAsync(panel, loading);

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
            return [Card(new TextBlock { Text = "No renameable network adapters found.", Opacity = 0.75 })];

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

    // ── Maintenance ────────────────────────────────────────────────────────────────

    private UIElement BuildMaintenanceSection()
    {
        var panel = Section("Maintenance");

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

        panel.Children.Add(SettingRow(
            "Config & logs",
            "Open the raw config.json or the log file, or re-read config.json from disk after an out-of-band edit.",
            buttons));

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

        var aboutBtn = new Button { Content = $"About {AppInfo.Name}" };
        aboutBtn.Click += (_, _) => ShowAboutWindow();
        panel.Children.Add(SettingRow(
            AppInfo.Name,
            "Automatically connects Hyper-V VMs to the right virtual switch when the host changes " +
            "networks. Manage VM power and state directly from the system tray. Made by ZeroZero Software.",
            aboutBtn));

        return panel;
    }

    private void ShowAboutWindow()
    {
        if (_closed) return;
        if (_aboutWindow is not null) { _aboutWindow.Activate(); return; }

        var options = new BrandAboutOptions
        {
            Info = new AboutInfo
            {
                AppName     = AppInfo.Name,
                Version     = AppInfo.Version,
                Description = "Automatically connects Hyper-V VMs to the right virtual switch when the host changes networks. Manage VM power and state directly from the system tray.",
                RepoUrl     = "https://github.com/0z00z0/HyperVManagerTray",
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
            // Reuses this window's own update flow (UpdatePrompt.RunAsync). Returns false: the Inno
            // installer restarts the app itself, so the About window never needs to trigger a self-exit.
            OnCheckForUpdates = async () => { await CheckForUpdatesAsync(); return false; },
        };

        _aboutWindow = new BrandAboutWindow(options);
        _aboutWindow.Closed += (_, _) => _aboutWindow = null;
        _aboutWindow.Activate();
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
