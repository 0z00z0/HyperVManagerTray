using System.Net.Http;
using H.NotifyIcon;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
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
    private Window?          _hostWindow;
    private TaskbarIcon?     _trayIcon;
    private DashboardWindow? _dashboard;
    private TrayMenu?        _menu;

    private string _exeDir = AppContext.BaseDirectory;
    private bool?  _bridged;  // null = icon not yet initialized; ensures first switch always updates
    private System.Drawing.Icon? _iconImage;

    // ── Silent-teardown self-heal ───────────────────────────────────────────────
    // Confirmed 2026-07-05: a GPU driver fault during a power-source change (WER LiveKernelEvent
    // Kernel_141) tore down the WinUI/Mica compositor and terminated the tray as a CLEAN exit —
    // no exception, no WER app record, no crash.log, no minidump. These flags let OnProcessExit
    // tell that silent teardown apart from the two legitimate exits (tray Exit, logoff/shutdown)
    // so only the illegitimate one relaunches a fresh instance.
    private static volatile bool _intentionalExit;
    private static volatile bool _sessionEnding;
    private static readonly DateTime _processStartUtc = DateTime.UtcNow;
    private static Mutex? _singleInstanceMutex;
    private const string SingleInstanceMutexName = @"Local\HyperVManagerTray.SingleInstance";
    private const string AutoRelaunchArg = "--auto-relaunch";

    public App()
    {
        InitializeComponent();

        // A tray app's lifetime is anchored to the tray icon (a Win32 construct), NOT to any XAML
        // window. With the default OnLastWindowClose policy, a GPU/compositor reset that destroys
        // all our windows from below tears the whole process down as a clean exit (the "vanished
        // tray, zero trace" crash above). OnExplicitShutdown keeps the process alive through that;
        // the dashboard recreates itself lazily on the next tray click (see ToggleDashboard).
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
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
        if (!AcquireSingleInstanceLock())
        {
            _intentionalExit = true;   // a duplicate exit must NOT trigger the self-heal relaunch
            Exit();
            return;
        }

        // A logoff/shutdown ProcessExit is legitimate — don't let it trigger the self-heal relaunch.
        Microsoft.Win32.SystemEvents.SessionEnding += (_, _) => _sessionEnding = true;

        // Opt native Win32 elements (tray context menu, etc.) into system dark mode.
        // Must run before any UI is created so the menu HWND inherits the setting.
        NativeMethods.EnableDarkModeForNativeUi();

        // When resurrected after a GPU-reset teardown, the display subsystem may still be
        // recovering — wait a beat before creating windows/the tray icon so the fresh instance
        // doesn't die to the same reset it was born from.
        if (Environment.GetCommandLineArgs().Contains(AutoRelaunchArg))
            await Task.Delay(TimeSpan.FromSeconds(5));

        _ui         = DispatcherQueue.GetForCurrentThread();
        _hostWindow = new MainWindow();   // never shown; a secondary keep-alive alongside OnExplicitShutdown

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
                _intentionalExit = true;   // deliberate give-up — relaunching would just fail again
                Exit();
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
            _intentionalExit = true;   // startup failed — relaunching would just fail again
            Exit();
        }
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
        try
        {
            Directory.CreateDirectory(AppInfo.DataDir);
            File.AppendAllText(
                AppInfo.CrashLog,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [CRASH] {source}: {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* never throw from the crash logger */ }
    }

    // ── Single-instance + self-heal relaunch ────────────────────────────────────

    /// <summary>
    /// Acquires the process-wide single-instance mutex, retrying briefly to ride out the window
    /// where a self-heal relaunch overlaps the still-terminating old instance. Returns false only
    /// if another instance genuinely holds it.
    /// </summary>
    private static bool AcquireSingleInstanceLock()
    {
        for (int attempt = 0; attempt < 15; attempt++)
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: false, SingleInstanceMutexName);
            try
            {
                if (_singleInstanceMutex.WaitOne(TimeSpan.Zero)) return true;
            }
            catch (AbandonedMutexException)
            {
                // Previous owner died abnormally (exactly our GPU-teardown crash) — we now hold it.
                return true;
            }
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Thread.Sleep(200);
        }
        return false;
    }

    /// <summary>
    /// Fires on every CLEAN process teardown (NOT on a hard kill / taskkill, which is what we want —
    /// a Task Manager kill should stay dead). If the exit was neither user-initiated (tray Exit) nor
    /// a logoff/shutdown, it is the silent compositor-loss teardown — relaunch a fresh instance
    /// (rate-limited so a persistent fault can't spin a relaunch loop). The dying process is already
    /// elevated, so the child inherits elevation without a UAC prompt.
    /// </summary>
    private void OnProcessExit(object? sender, EventArgs e)
    {
        if (_intentionalExit || _sessionEnding) return;

        var uptime = DateTime.UtcNow - _processStartUtc;
        if (!TryRecordRelaunch())
        {
            AppendLifecycleLine($"Unexpected teardown after {uptime:hh\\:mm\\:ss}; 3 relaunches within 10 min — giving up.");
            return;
        }

        AppendLifecycleLine($"Unexpected silent teardown after {uptime:hh\\:mm\\:ss} (likely GPU/compositor reset) — relaunching.");
        try
        {
            if (Environment.ProcessPath is { } exe)
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(exe, AutoRelaunchArg) { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            AppendLifecycleLine($"Relaunch failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Sliding-window rate limiter for the self-heal relaunch: false once 3 relaunches have happened
    /// in the last 10 minutes. Timestamps persist in a file because each check runs in a NEW process,
    /// so in-memory state can't span the relaunch chain it is limiting.
    /// </summary>
    private static bool TryRecordRelaunch()
    {
        try
        {
            var path   = Path.Combine(AppInfo.DataDir, "relaunch-history.txt");
            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds();
            var recent = new List<long>();
            if (File.Exists(path))
                foreach (var line in File.ReadAllLines(path))
                    if (long.TryParse(line, out var ts) && ts >= cutoff) recent.Add(ts);

            if (recent.Count >= 3) return false;

            recent.Add(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Directory.CreateDirectory(AppInfo.DataDir);
            File.WriteAllLines(path, recent.Select(t => t.ToString()));
            return true;
        }
        catch
        {
            return true;   // if the bookkeeping itself fails, err on the side of bringing the tray back
        }
    }

    /// <summary>Appends a lifecycle line to crash.log directly (the DI logger may be torn down at ProcessExit). Never throws.</summary>
    private static void AppendLifecycleLine(string msg)
    {
        try
        {
            Directory.CreateDirectory(AppInfo.DataDir);
            File.AppendAllText(AppInfo.CrashLog,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [LIFECYCLE] {msg}{Environment.NewLine}");
        }
        catch { /* never throw from the exit path */ }
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

    /// <summary>
    /// Builds a multi-line tooltip showing the active virtual switch and each VM's first IPv4
    /// address, then posts it to the UI thread.  Never throws.
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

            var lines = new System.Collections.Generic.List<string>
            {
                AppInfo.Name,
                TruncateLine($"Switch: {switchName}", 63),
            };

            foreach (var name in vmNames)
            {
                if (_vm.GetCachedVmIp(name) is { } ip)
                    lines.Add(TruncateLine($"{name}: {ip}", 63));
            }

            var tooltip = string.Join("\n", lines);
            // Win32 balloon-tip tooltip hard limit is 127 chars total.
            if (tooltip.Length > 127)
                tooltip = tooltip[..126] + "…";

            _ui.TryEnqueue(() => _trayIcon.ToolTipText = tooltip);
        }
        catch (Exception ex)
        {
            // Best-effort — never let a tooltip failure surface to the user.
            try { _ui.TryEnqueue(() => _trayIcon.ToolTipText = AppInfo.Name); } catch { }
            _ = ex; // suppress unused-variable warning
        }
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
        // User-initiated exit is legitimate — OnProcessExit must NOT self-heal relaunch it.
        _intentionalExit = true;
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
