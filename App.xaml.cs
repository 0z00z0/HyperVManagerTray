using System.Net.Http;
using H.NotifyIcon;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using HyperVManagerTray.Helpers;
using HyperVManagerTray.Services;
using HyperVManagerTray.UI;

namespace HyperVManagerTray;

/// <summary>
/// Application entry point.  Owns the long-lived services (config / Hyper-V / network monitor),
/// the tray icon, and the dashboard popup.  Replaces the old WinForms <c>Program</c> +
/// <c>TrayApplication</c>.  A hidden host window keeps the WinUI app alive while only the tray
/// icon is visible.
/// </summary>
public partial class App : Application
{
    private ILoggerFactory? _loggerFactory;
    private ConfigManager?  _config;
    private HyperVManager?  _hyperV;   // Phase 1: switch binding / host-vNIC repair only (PowerShell)
    private VmService?      _vm;       // VM status/metrics/power/IPs via WMI
    private NetworkMonitor? _monitor;
    private StartupManager  _startup = null!;
    private HttpClient?     _httpClient;
    private UpdateChecker?  _updateChecker;

    private DispatcherQueue  _ui = null!;
    private TaskbarIcon?     _trayIcon;
    private DashboardWindow? _dashboard;
    private TrayMenu?        _menu;

    private string _exeDir = AppContext.BaseDirectory;
    private bool?  _bridged;  // null = icon not yet initialized; ensures first switch always updates
    private System.Drawing.Icon? _iconImage;

    public App()
    {
        InitializeComponent();

        // A tray app's lifetime is anchored to the tray icon (a Win32 construct), NOT to any XAML
        // window. With the default OnLastWindowClose policy, a GPU/compositor reset that destroys
        // all our windows from below tears the whole process down as a clean exit (the "vanished
        // tray, zero trace" crash — see Helpers/SelfHealWatchdog.cs). OnExplicitShutdown keeps the
        // process alive through that; the dashboard recreates itself lazily on the next tray click
        // (see ToggleDashboard). SelfHealWatchdog covers the case where the process dies anyway.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        SelfHealWatchdog.Install();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Handlers FIRST, before anything that can throw: a WinUI tray app that throws on the
        // dispatcher thread otherwise dies silently (stowed exception in CoreMessagingXP.dll) with
        // nothing in the log. Registering first also lets a UI-thread startup exception be marked
        // Handled — the process survives instead of dying and tripping the self-heal relaunch.
        RegisterGlobalExceptionHandlers();

        // Single-instance next — before any window or tray icon. A self-heal relaunch can spawn
        // the new instance while the old one is still milliseconds from terminating, and a
        // resume-from-standby can double-launch via the logon task; two tray processes would both
        // bind virtual switches and race. Held for the process lifetime; the OS releases it on
        // termination (clean exit, crash, or kill).
        if (!SelfHealWatchdog.AcquireLock())
        {
            ExitIntentionally();   // a duplicate exit must NOT trigger the self-heal relaunch
            return;
        }

        // Opt native Win32 elements (tray context menu, etc.) into system dark mode.
        // Must run before any UI is created so the menu HWND inherits the setting.
        NativeMethods.EnableDarkModeForNativeUi();

        // When resurrected after a GPU-reset teardown, the display subsystem may still be
        // recovering — wait a beat before creating windows/the tray icon so the fresh instance
        // doesn't die to the same reset it was born from.
        if (SelfHealWatchdog.IsAutoRelaunch)
            await Task.Delay(TimeSpan.FromSeconds(5));

        _ui = DispatcherQueue.GetForCurrentThread();
        _ = new MainWindow();   // never shown; keeps a XAML window around (OnExplicitShutdown owns the real keep-alive)

        try
        {
            var configPath = ConfigManager.GetConfigPath();

            var logDir = AppInfo.DataDir;
            Directory.CreateDirectory(logDir);
            _loggerFactory = LoggerFactory.Create(b =>
            {
                // Minimum level comes from config.json (defaults to Debug). Read directly here — the
                // logger must exist before the full ConfigManager (which needs a logger) is built.
                b.SetMinimumLevel(ConfigManager.ReadLogLevel(configPath));
                b.AddSimpleFileLogger(AppInfo.LogFile);
            });

            // Capture a minidump if the app dies from a NATIVE fault (GDI+, comctl32, the
            // WinUI/Mica compositor during a dock/display/power transition, …).  Those bypass
            // the managed handlers below, so without this a crash leaves no trace at all.
            CrashDumps.TryRegisterLocalDumps(Path.Combine(logDir, "dumps"));

            _startup       = new StartupManager(_loggerFactory.CreateLogger<StartupManager>());
            _httpClient    = new HttpClient();
            _updateChecker = new UpdateChecker(_httpClient, _loggerFactory.CreateLogger<UpdateChecker>());

            if (!File.Exists(configPath))
            {
                NativeMethods.Error(
                    $"config.json not found at:\n{configPath}\n\nPlace config.json next to the executable and restart.",
                    AppInfo.Name);
                ExitIntentionally();   // deliberate give-up — relaunching would just fail again
                return;
            }

            _exeDir  = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            _config  = new ConfigManager(configPath, _loggerFactory.CreateLogger<ConfigManager>());
            _hyperV  = new HyperVManager(_loggerFactory.CreateLogger<HyperVManager>());
            _vm      = new VmService(_loggerFactory.CreateLogger<VmService>());
            _monitor = new NetworkMonitor(_config, _hyperV, _vm, _loggerFactory.CreateLogger<NetworkMonitor>());

            InitTrayIcon();

            _monitor.SwitchApplied += OnSwitchApplied;
            _monitor.Start();

            // Populate the tooltip immediately — before the first SwitchApplied fires.
            _ = UpdateTooltipAsync();

            // Pre-warm the VM discovery cache so the right-click menu never blocks the UI
            // thread on first open.  Runs on the thread-pool; rebuilds the menu on the UI
            // thread once data arrives.  Failures are silently swallowed.
            _ = PreWarmVmCacheAsync();

            // Background startup update check — inserts a badge at the top of the tray menu
            // if a newer GitHub release exists.  Never blocks startup; failures are silent.
            _ = CheckForUpdatesOnStartupAsync();

            // Once initial binding has settled, clean up any orphaned management vNICs left on
            // the rule switches by older builds.  Idle-guarded, so it never disturbs a live link.
            _ = HealSwitchOrphansOnStartupAsync();

            // Pre-create and prime the dashboard so its first real open has no white flash:
            // the Mica window's initial (white) composition frame happens now, off-screen.
            CreateDashboard(prime: true);
        }
        catch (Exception ex)
        {
            NativeMethods.Error($"Failed to start Hyper-V Manager Tray:\n\n{ex}", AppInfo.Name);
            ExitIntentionally();   // startup failed — relaunching would just fail again
        }
    }

    /// <summary>Exits after telling <see cref="SelfHealWatchdog"/> this teardown is deliberate, so it doesn't relaunch.</summary>
    private void ExitIntentionally()
    {
        SelfHealWatchdog.MarkLegitimateExit();
        Exit();
    }

    // ── Global crash logging ────────────────────────────────────────────────────

    /// <summary>
    /// Wires the three process-wide exception sinks so a crash is never silent.
    /// UI/XAML-thread exceptions are logged and marked handled (the tray survives);
    /// background and unobserved-task exceptions are logged before the runtime acts.
    /// </summary>
    private void RegisterGlobalExceptionHandlers()
    {
        // UI/XAML dispatcher thread — keep the tray alive instead of a silent stowed-exception crash.
        UnhandledException += (_, e) =>
        {
            LogCrash("UI/XAML UnhandledException", e.Exception);
            e.Handled = true;
        };

        // Background / finalizer threads — can't stop termination, but log and notify the user.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            LogCrash("AppDomain UnhandledException (fatal)", ex);
            NativeMethods.Error(
                $"Hyper-V Manager Tray crashed and needs to close.\n\n" +
                $"{ex?.Message ?? "Unknown error"}\n\n" +
                $"Details written to crash.log in %AppData%\\{AppInfo.Id}.",
                AppInfo.Name);
        };

        // Faulted Tasks whose exception was never awaited/observed.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>Appends a full exception dump to crash.log (and the normal log if available). Never throws.</summary>
    private void LogCrash(string source, Exception? ex)
    {
        try { _loggerFactory?.CreateLogger("Crash").LogError(ex, "Unhandled exception ({Source})", source); }
        catch { /* logging must never throw */ }
        AppInfo.AppendCrashLogLine("CRASH", $"{source}: {ex}{Environment.NewLine}");
    }

    // ── Tray icon ───────────────────────────────────────────────────────────────

    private void InitTrayIcon()
    {
        _trayIcon = (TaskbarIcon)Resources["TrayIcon"];
        SetTrayIcon(null);  // grey = unknown until first SwitchApplied fires

        _menu = new TrayMenu(_config!, _monitor!, _hyperV!, _vm!, _startup, _updateChecker!, OnExit);
        _trayIcon.ContextFlyout     = _menu.Flyout;
        _trayIcon.LeftClickCommand  = new RelayCommand(ToggleDashboard);
        _trayIcon.RightClickCommand = new RelayCommand(() => _menu!.RefreshState());

        _trayIcon.ForceCreate();
    }

    // ── Switch-applied → update icon + open dashboard ───────────────────────────

    private void OnSwitchApplied(object? sender, MatchResult result)
    {
        _ui.TryEnqueue(() =>
        {
            try
            {
                bool bridged = result.VirtualSwitch != _config!.Current.Fallback.VirtualSwitch;
                if (bridged != _bridged)
                {
                    _bridged = bridged;
                    SetTrayIcon(bridged);
                }
                _dashboard?.OnSwitchApplied(result);
            }
            catch (Exception ex) { LogCrash("OnSwitchApplied UI update", ex); }
        });

        // Update tooltip with the new switch + fresh VM IPs (runs on thread-pool; posts to UI when done).
        _ = UpdateTooltipAsync();
    }

    // Segoe Fluent Icons glyphs (verified against Microsoft's published codepoint table at
    // https://learn.microsoft.com/en-us/windows/apps/design/iconography/segoe-fluent-icons-font —
    // do not change these without re-checking that table; a wrong codepoint renders as a blank box).
    private const string GlyphAppInfo       = ""; // "Info"            — app identity / version row
    private const string GlyphNetworkSwitch = ""; // "NetworkAdapter"  — active virtual switch row
    private const string GlyphVm            = ""; // "Devices2"        — per-VM name/IP row

    /// <summary>
    /// Builds the tray hover tooltip showing app name+version, the active virtual switch, and each
    /// VM's first IPv4 address, then posts it to the UI thread. Sets both the plain-text
    /// <see cref="TaskbarIcon.ToolTipText"/> (127-char Win32 limit; also what screen readers read)
    /// and the rich icon-annotated <see cref="TaskbarIcon.TrayToolTip"/> content. Never throws.
    /// </summary>
    private async Task UpdateTooltipAsync()
    {
        if (_vm is null || _config is null || _trayIcon is null) return;

        try
        {
            var switchName = _monitor?.LastApplied?.VirtualSwitch ?? "No switch";
            var vmNames    = _config.Current.VirtualMachines.Select(v => v.Name).ToList();
            // Refresh the WMI caches once (background), then read the per-VM IPs synchronously.
            await _vm.RefreshOnceAsync().ConfigureAwait(false);

            var vmRows = new System.Collections.Generic.List<(string Name, string Ip)>();
            foreach (var name in vmNames)
            {
                if (_vm.GetCachedVmIp(name) is { } ip)
                    vmRows.Add((name, ip));
            }

            var appLine = $"{AppInfo.Name} v{AppInfo.Version}";

            var lines = new System.Collections.Generic.List<string>
            {
                appLine,
                TruncateLine($"Switch: {switchName}", 63),
            };
            foreach (var (name, ip) in vmRows)
                lines.Add(TruncateLine($"{name}: {ip}", 63));

            var tooltip = string.Join("\n", lines);
            // Win32 balloon-tip tooltip hard limit is 127 chars total.
            if (tooltip.Length > 127)
                tooltip = tooltip[..126] + "…";

            _ui.TryEnqueue(() =>
            {
                _trayIcon.ToolTipText = tooltip;
                _trayIcon.TrayToolTip = BuildRichTooltip(appLine, switchName, vmRows);
            });
        }
        catch (Exception ex)
        {
            // Best-effort — never let a tooltip failure surface to the user.
            try { _ui.TryEnqueue(() => _trayIcon.ToolTipText = AppInfo.Name); } catch { }
            _ = ex; // suppress unused-variable warning
        }
    }

    /// <summary>
    /// Builds the icon-annotated content for <see cref="TaskbarIcon.TrayToolTip"/>: an app+version
    /// row, the active virtual switch row, and one row per VM with a known IP — mirroring
    /// <see cref="UpdateTooltipAsync"/>'s plain-text content, styled to match the dashboard's
    /// existing Mica-card look (<see cref="AppColors"/> / theme brushes, imperative construction —
    /// see <c>DashboardWindow.BuildCard</c>).
    /// </summary>
    private static UIElement BuildRichTooltip(
        string appLine, string switchName, System.Collections.Generic.IReadOnlyList<(string Name, string Ip)> vmRows)
    {
        var panel = new StackPanel { Spacing = 6, Padding = new Thickness(12, 10, 12, 10), MaxWidth = 280 };

        panel.Children.Add(BuildTooltipRow(GlyphAppInfo, appLine, emphasize: true));
        panel.Children.Add(BuildTooltipRow(GlyphNetworkSwitch, TruncateLine($"Switch: {switchName}", 80)));
        foreach (var (name, ip) in vmRows)
            panel.Children.Add(BuildTooltipRow(GlyphVm, TruncateLine($"{name}: {ip}", 80)));

        return panel;
    }

    /// <summary>Builds a single "glyph + text" tooltip row (Segoe Fluent Icons FontIcon + TextBlock).</summary>
    private static StackPanel BuildTooltipRow(string glyph, string text, bool emphasize = false)
    {
        var icon = new FontIcon
        {
            FontFamily        = new FontFamily("Segoe Fluent Icons"),
            Glyph             = glyph,
            FontSize          = 14,
            Width             = 18,
            Foreground        = AppColors.IndicatorGreyBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var label = new TextBlock
        {
            Text              = text,
            FontSize          = 12,
            FontWeight        = emphasize ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            Foreground        = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
            TextTrimming      = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(icon);
        row.Children.Add(label);
        return row;
    }

    /// <summary>
    /// Pre-warms the VM discovery cache on startup so the first right-click menu open is
    /// instant.  Calls <see cref="TrayMenu.RefreshState"/> once the cache is populated.
    /// </summary>
    private async Task PreWarmVmCacheAsync()
    {
        if (_vm is null || _menu is null) return;
        try
        {
            await _vm.RefreshOnceAsync().ConfigureAwait(false);
            // Cache is now populated — rebuild the menu on the UI thread so any previously
            // shown "Loading VMs…" placeholder is replaced with the real VM list.
            _ui.TryEnqueue(() => _menu.RefreshState());
        }
        catch { /* non-fatal — menu will retry on next right-click */ }
    }

    /// <summary>
    /// Runs a silent update check in the background.  If a newer release exists on GitHub the
    /// tray menu badge is set on the UI thread.  Network / parse failures are swallowed.
    /// </summary>
    private async Task CheckForUpdatesOnStartupAsync()
    {
        if (_updateChecker is null || _menu is null) return;
        try
        {
            var result = await _updateChecker.CheckAsync().ConfigureAwait(false);
            if (result.UpdateAvailable)
                _ui.TryEnqueue(() => _menu.SetUpdateBadge(result));
        }
        catch { /* never surface a background check failure */ }
    }

    /// <summary>
    /// A short while after startup (once initial binding has settled), collapses any duplicate
    /// host vNICs on the rule switches back to one (see <see cref="HyperVManager.RepairHostVNicAsync"/>),
    /// repairing the "host offline but VM online" state a prior dock cycle may have left behind.
    /// Best-effort; all failures are swallowed.
    /// </summary>
    private async Task HealSwitchOrphansOnStartupAsync()
    {
        if (_hyperV is null || _config is null) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);

            foreach (var sw in _config.Current.RuleSwitches)
                await _hyperV.RepairHostVNicAsync(sw).ConfigureAwait(false);
        }
        catch { /* best-effort cleanup; never surface */ }
    }

    /// <summary>Truncates <paramref name="line"/> to <paramref name="maxLen"/> chars, appending "…" if trimmed.</summary>
    private static string TruncateLine(string line, int maxLen) =>
        line.Length <= maxLen ? line : line[..(maxLen - 1)] + "…";

    /// <summary>Swaps the tray icon based on network state, disposing the previous one.</summary>
    private void SetTrayIcon(bool? bridged)
    {
        var state = bridged switch
        {
            true  => TrayIconState.Bridged,
            false => TrayIconState.Fallback,
            null  => TrayIconState.Unknown,
        };
        var previous = _iconImage;
        _iconImage = new System.Drawing.Icon(IconGenerator.GenerateAndSave(_exeDir, state));
        _trayIcon!.Icon = _iconImage;
        previous?.Dispose();
    }

    // ── Dashboard ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the dashboard popup. Normally kept alive for the app's lifetime — the window cancels
    /// close requests and hides instead (see <see cref="DashboardWindow.AllowClose"/>), so every
    /// open reuses the already-composed window and never shows the Mica white-flash. Priming does an
    /// off-screen first-composition to kill the flash on the very first open; a lazy recreation
    /// after a GPU-reset destroy skips priming (it's about to be shown anyway).
    /// </summary>
    private void CreateDashboard(bool prime)
    {
        _dashboard = new DashboardWindow(_config!, _monitor!, _hyperV!, _vm!);
        _dashboard.Closed += (_, _) => _dashboard = null;
        if (prime) _dashboard.Prime();
    }

    private void ToggleDashboard()
    {
        // Normally a primed singleton; but a GPU/compositor reset can destroy its window out from
        // under us (Closed → _dashboard = null). Recreate it so the tray click still works — the
        // process itself survives via OnExplicitShutdown.
        if (_dashboard is null) CreateDashboard(prime: false);
        var dash = _dashboard!;   // CreateDashboard just assigned it

        if (dash.AppWindow.IsVisible)
            dash.HideWindow();
        else if (!dash.HiddenByThisClick)
            // HiddenByThisClick filters out exactly one case: the tray click that is itself
            // the deactivation that just auto-hid the popup (that click means "close", not
            // "reopen").  Any other click — including a quick dismiss-elsewhere-then-tray
            // sequence — shows the window.
            dash.ShowNearTray();
    }

    // ── Lifecycle ───────────────────────────────────────────────────────────────

    private void OnExit()
    {
        // User-initiated exit is legitimate — the self-heal watchdog must NOT relaunch it.
        SelfHealWatchdog.MarkLegitimateExit();
        // Let the persistent dashboard actually close now (it otherwise cancels close → hide).
        if (_dashboard is not null) _dashboard.AllowClose = true;
        _trayIcon?.Dispose();
        _iconImage?.Dispose();
        _monitor?.Dispose();
        _vm?.Dispose();
        _config?.Dispose();
        _hyperV?.Dispose();
        _httpClient?.Dispose();
        _loggerFactory?.Dispose();
        Exit();
    }
}
