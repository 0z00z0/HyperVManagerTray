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
    private LogLevelSwitch? _logLevelSwitch;   // live minimum level for all file logs (issue #22)
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
    // The icon state currently posted. Null = not yet initialised, so the first apply always updates.
    // Holds the rendered STATE (not "is it bridged?") because a failed apply is now its own state that
    // no boolean could express (issue #37).
    private TrayIconState? _iconState;
    private System.Drawing.Icon? _iconImage;

    // Last tooltip text actually posted, so event-driven rebuilds only touch the UI on a real change
    // (VmService raises StatusesChanged on every refresh, incl. the dashboard's 2.5 s metrics loop).
    private string? _lastTooltip;
    private readonly object _tooltipLock = new();

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

            // Single live verbosity gate (issue #22): initialised from config.json's logLevel, then
            // consulted per write by the file logger so a Settings/config change applies with no
            // restart. Read directly here — the switch must exist before the full ConfigManager
            // (which updates it on every reload) is built.
            _logLevelSwitch = new LogLevelSwitch(ConfigManager.ReadLogLevel(configPath));

            _loggerFactory = LoggerFactory.Create(b =>
            {
                // Pin the factory minimum to Trace so it never pre-filters ahead of the live switch;
                // _logLevelSwitch is the sole runtime gate for all three logs (issue #22).
                b.SetMinimumLevel(LogLevel.Trace);
                // Category-routing sink: "vm-power" → vm-power.log (issue #20), "ui" → ui.log
                // (issue #21); everything else → switcher.log. All gated by the live switch.
                b.AddSimpleFileLogger(AppInfo.LogFile, new Dictionary<string, string>
                {
                    ["vm-power"] = AppInfo.VmPowerLog,
                    ["ui"]       = AppInfo.UiLog,
                }, _logLevelSwitch);
            });

            // Capture a minidump if the app dies from a NATIVE fault (GDI+, comctl32, the
            // WinUI/Mica compositor during a dock/display/power transition, …).  Those bypass
            // the managed handlers below, so without this a crash leaves no trace at all.
            CrashDumps.TryRegisterLocalDumps(Path.Combine(logDir, "dumps"));

            _startup       = new StartupManager(_loggerFactory.CreateLogger<StartupManager>());
            _httpClient    = new HttpClient();
            _updateChecker = new UpdateChecker(_httpClient, _loggerFactory.CreateLogger<UpdateChecker>());

            // Shared "vm-power" category logger → vm-power.log (issue #20): the begin+outcome audit
            // trail for every VM power action, from both the user (VmService) and automatic triggers
            // (NetworkMonitor autostart / on-bridge-lost).
            var powerLog = _loggerFactory.CreateLogger("vm-power");

            // Shared "ui" category logger → ui.log (issue #21): tray/window/rename UI events plus
            // ConfigManager's settings-change lines. Set the static UI gateway before any UI exists.
            var uiLog = _loggerFactory.CreateLogger("ui");
            UI.UiActivityLog.Logger = uiLog;

            _exeDir  = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

            // A missing config.json is no longer fatal (issue #38): write the blank-slate default and
            // carry on into the app, whose Settings editor and tray flows are how the first VM and rule
            // get added anyway. Done here — before the ConfigManager and its watcher exist — so the
            // write can't raise ConfigReloaded and kick off a switch re-evaluation. Balloon'd (not
            // error-boxed) once the tray icon is up, so the user knows a file appeared next to the exe.
            bool createdDefaultConfig = ConfigManager.CreateDefaultIfMissing(configPath, uiLog);

            _config  = new ConfigManager(configPath, uiLog, _logLevelSwitch);
            _hyperV  = new HyperVManager(_loggerFactory.CreateLogger<HyperVManager>());
            _vm      = new VmService(_loggerFactory.CreateLogger<VmService>(), powerLog);
            _monitor = new NetworkMonitor(_config, _hyperV, _vm, _loggerFactory.CreateLogger<NetworkMonitor>(), powerLog);

            InitTrayIcon();

            // Config feedback needs the tray icon, so it lands here rather than at the load itself.
            AnnounceConfigState(createdDefaultConfig);

            // A later hand-edit that doesn't parse is announced once per broken save (issue #39) — the
            // app keeps running on the previous settings, and staying quiet about that is how a user
            // ends up debugging dock behaviour against rules that were never loaded.
            _config.ConfigLoadFailed += (_, outcome) =>
                ShowBalloon($"{AppInfo.Name} — config", ConfigLoadUi.BalloonMessage(outcome)!,
                            isError: true, suppressWhenDashboardVisible: false);

            _monitor.SwitchApplied += OnSwitchApplied;
            _monitor.Start();

            // Drive the tray tooltip off VmService's push channel (issue #16, conversion #2):
            //   • OnVmStatuses rebuilds the tooltip from VmService's caches whenever a refresh raises
            //     StatusesChanged, so it reacts to real changes instead of a fixed poll.
            //   • SubscribeStateWatcher keeps a lightweight, always-on state-change watcher running
            //     (NO 2.5 s metrics loop — that stays dashboard-gated), so a VM state change pushes
            //     the tooltip even while the dashboard is closed, without a permanent metrics poll.
            _vm.StatusesChanged += OnVmStatuses;
            // Surface a failed power action as a tray balloon when the dashboard isn't up to show it
            // inline (issue #30, finding 2) — a tray-initiated Start/Shutdown/… that fails is otherwise
            // completely silent.
            _vm.OperationProgress += OnVmOperationFailed;
            _vm.SubscribeStateWatcher();

            // Show something immediately (caches may still be warming — PreWarmVmCacheAsync fills them).
            PostTooltipFromCaches();

            // Safety net for the ONE tooltip input the state watcher can't push: a guest DHCP IP that
            // appears/changes with no VM state transition (Msvm_ComputerSystem raises no event for it).
            // A slow periodic refresh catches that drift; state changes no longer depend on it. Kept at
            // 60 s to preserve the previous worst-case IP-appearance latency — see TooltipRefreshLoopAsync.
            _ = TooltipRefreshLoopAsync();

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

    /// <summary>
    /// Tells the user, once, what happened to config.json at startup — the two cases that used to be
    /// silent or fatal:
    /// <list type="bullet">
    ///   <item><b>Created</b> (issue #38): there was no config, so the app wrote a blank-slate one.
    ///         A balloon, not an error box: nothing is wrong, a file simply appeared.</item>
    ///   <item><b>Corrupt</b> (issue #39): a file exists but doesn't parse. The app would otherwise
    ///         start silently on an empty in-memory default — every rule the user wrote quietly absent,
    ///         with no signal beyond a log line. Say so.</item>
    /// </list>
    /// Not suppressed by a visible dashboard: neither fact is shown anywhere else.
    /// </summary>
    private void AnnounceConfigState(bool createdDefault)
    {
        if (createdDefault)
        {
            ShowBalloon(AppInfo.Name,
                        "No config.json was found, so a default one was created next to the app. "
                        + "Add your VMs and network rules from Settings.",
                        isError: false, suppressWhenDashboardVisible: false);
            return;
        }

        if (ConfigLoadUi.BalloonMessage(_config!.LastLoad) is { } problem)
            ShowBalloon($"{AppInfo.Name} — config", problem,
                        isError: true, suppressWhenDashboardVisible: false);
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
        SetTrayIcon(TrayIconState.Unknown);  // grey = unknown until the first apply reports an outcome

        // The tray's manual network actions report through the same balloon channel a failed apply uses
        // (issue #37). Not suppressed by a visible dashboard: unlike an automatic apply, these are direct
        // answers to something the user just clicked, and must never be swallowed.
        _menu = new TrayMenu(_config!, _monitor!, _hyperV!, _vm!, _startup, _updateChecker!, OnExit,
                             (title, message, isError) =>
                                 ShowBalloon(title, message, isError, suppressWhenDashboardVisible: false));
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
                // The rules' INTENT (a non-fallback switch was picked) only decides WHICH success colour
                // to use once the apply is confirmed — NetworkStatusUi.IconFor gates it on the outcome.
                // Deriving the icon straight from result.VirtualSwitch, as this did before issue #37, is
                // what let a failed bind show a confident green "bridged".
                bool bridgedTarget = result.VirtualSwitch != _config!.Current.Fallback.VirtualSwitch;
                var  state         = NetworkStatusUi.IconFor(result.ApplyStatus, bridgedTarget);
                if (state != _iconState)
                {
                    _iconState = state;
                    SetTrayIcon(state);
                }
                _dashboard?.OnSwitchApplied(result);
            }
            catch (Exception ex) { LogCrash("OnSwitchApplied UI update", ex); }
        });

        // A failed apply is otherwise only a log line (issue #37) — balloon it, exactly as a failed VM
        // power action does. Auto-switching is the app's core job; when it silently fails the user
        // debugs "the VM has no network" from scratch while every surface says everything is fine.
        NotifyIfApplyFailed(result);

        // The app just (re)bound a virtual switch — the one switch-change the state watcher can't see —
        // so invalidate VmService's switch cache, show the new switch name at once, and kick a refresh
        // for fresh VM IPs (which lands back on the tooltip via OnVmStatuses → StatusesChanged).
        _vm?.InvalidateSwitchCache();
        PostTooltipFromCaches();
        _ = _vm?.RefreshOnceAsync();
    }

    /// <summary>
    /// <summary>
    /// <see cref="VmService.StatusesChanged"/> handler — the tooltip's event source. Fires on a
    /// background thread after a refresh has already updated VmService's caches, so it just rebuilds
    /// the tooltip from those caches (no WMI) and posts it. Together with the always-on
    /// <see cref="VmService.SubscribeStateWatcher"/> this makes the tooltip track VM state changes
    /// even while the dashboard is closed (issue #16, conversion #2).
    /// </summary>
    private void OnVmStatuses(IReadOnlyList<Models.VmStatus> statuses) => PostTooltipFromCaches();

    /// <summary>
    /// Shows a tray balloon for a failed VM power action, but only when the dashboard isn't visible —
    /// the dashboard already surfaces the failure on the card, so a toast would be redundant there. This
    /// covers the tray VM-Power submenu path, whose failures were previously invisible (issue #30,
    /// finding 2). Fired on a background thread by <see cref="VmService.OperationProgress"/>; marshalled
    /// to the UI. Never throws.
    /// </summary>
    private void OnVmOperationFailed(Models.VmOperationProgress p)
    {
        if (p.Phase != Models.VmOpPhase.Failed) return;
        ShowBalloon($"{AppInfo.Name} — {p.VmName}",
                    string.IsNullOrWhiteSpace(p.Message) ? "Power action failed." : p.Message!,
                    isError: true,
                    suppressWhenDashboardVisible: true);
    }

    // The failure last balloon'd, so a persisting failure isn't re-announced on every NetworkChange.
    // SwitchApplied fires on every network blip — including the "no switch change needed" fast path,
    // which carries the previous outcome forward — so without this a single failed bind would toast
    // repeatedly while the user is doing nothing about it. Cleared on a successful apply, so a genuinely
    // NEW failure always announces itself.
    private string? _lastNotifiedFailure;

    /// <summary>
    /// Balloons a failed switch-apply (issue #37), mirroring <see cref="OnVmOperationFailed"/>: the
    /// dashboard shows the failure inline on the HOST NETWORK card, so the toast is suppressed while
    /// it's visible. Called on <see cref="NetworkMonitor"/>'s background thread; never throws.
    /// </summary>
    private void NotifyIfApplyFailed(MatchResult result)
    {
        try
        {
            var message = NetworkStatusUi.FailureMessage(
                result.ApplyStatus, result.VirtualSwitch, result.HostAdapterName, result.FailedVms);
            if (message is null)
            {
                _lastNotifiedFailure = null;   // recovered — re-arm for the next failure
                return;
            }

            if (message == _lastNotifiedFailure) return;   // same failure, still unresolved — say it once
            _lastNotifiedFailure = message;

            ShowBalloon($"{AppInfo.Name} — network", message,
                        isError: true, suppressWhenDashboardVisible: true);
        }
        catch (Exception ex) { LogCrash("Failed-apply tray toast", ex); }
    }

    /// <summary>
    /// Posts a tray balloon from any thread. <paramref name="suppressWhenDashboardVisible"/> skips it
    /// when the dashboard is up and already showing the same information inline. Never throws — a
    /// notification failure must not take the app down.
    /// </summary>
    private void ShowBalloon(string title, string message, bool isError, bool suppressWhenDashboardVisible)
    {
        _ui.TryEnqueue(() =>
        {
            try
            {
                if (suppressWhenDashboardVisible && _dashboard is { } d && d.AppWindow.IsVisible) return;
                _trayIcon?.ShowNotification(
                    title: title,
                    message: message,
                    icon: isError ? H.NotifyIcon.Core.NotificationIcon.Error
                                  : H.NotifyIcon.Core.NotificationIcon.Info);
            }
            catch (Exception ex) { LogCrash("Tray balloon", ex); }
        });
    }

    /// <summary>
    /// Rebuilds the tray tooltip from VmService's already-populated caches (no WMI call of its own)
    /// and posts it to the UI thread — but only when the visible text actually changed, since the
    /// dashboard's 2.5 s metrics loop also raises <see cref="VmService.StatusesChanged"/> and the
    /// tooltip content (switch + per-VM IPs) rarely moves. Never throws.
    /// </summary>
    private void PostTooltipFromCaches()
    {
        if (_vm is null || _config is null || _trayIcon is null) return;

        try
        {
            var tooltip = BuildTooltipText();
            lock (_tooltipLock)
            {
                if (tooltip == _lastTooltip) return;   // unchanged — skip the redundant UI post
                _lastTooltip = tooltip;
            }
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
    /// Assembles the tray hover tooltip (app name+version, the active virtual switch, each VM's first
    /// cached IPv4) from VmService's caches. Pure read — no WMI, no UI marshalling.
    ///
    /// A tray hover tooltip is plain Win32 text — <see cref="TaskbarIcon.TrayToolTip"/>'s rich
    /// WinUI-content mechanism does not reliably render on unpackaged WinUI 3 apps (confirmed via
    /// HavenDV/H.NotifyIcon issues #43/#91/#94 — it silently falls back to plain
    /// <see cref="TaskbarIcon.ToolTipText"/>). Colour emoji, not FontIcon glyphs, are how ChargeKeeper
    /// (this app's sibling) puts icons in its tray tooltip: the OS tooltip control renders emoji via
    /// the Segoe UI Emoji font fallback even inside a plain string. Same approach here.
    /// </summary>
    private string BuildTooltipText()
    {
        var applied    = _monitor?.LastApplied;
        var switchName = applied?.VirtualSwitch ?? "No switch";
        var vmNames    = _config!.Current.VirtualMachines.Select(v => v.Name).ToList();

        // The switch row states the OUTCOME, not just the intended switch (issue #37): a hover after a
        // failed bind must not read as a plain, healthy "Switch: Bridged". Empty for a confirmed apply.
        var switchSuffix = applied is null ? "" : NetworkStatusUi.TooltipSwitchSuffix(applied.ApplyStatus);

        var lines = new System.Collections.Generic.List<string>
        {
            // 🖥 Desktop-computer + app + version. Bracketed per Espen's request; matches
            // ChargeKeeper's two-space gap before the version marker.
            TruncateLine($"\U0001F5A5 {AppInfo.Name}  [{AppInfo.Version}]", 63),
            // 🔀 twisted arrows (a network "switch" routing traffic) + the active virtual switch.
            // Chosen over the globe so it doesn't read as a generic computer icon and conveys
            // switching/spread. Avoid dark/low-contrast emoji (e.g. a plug) on the dark Win11
            // tooltip background — see ChargeKeeper's App.xaml.cs UpdateTooltip note.
            // ⚠️ replaces it when the apply didn't land, so the row reads as a problem at a glance
            // (issue #37). The switch NAME absorbs the truncation, never the failure suffix — a
            // long switch name must not be able to hide "— bind failed".
            TruncateLine($"{(switchSuffix.Length == 0 ? "\U0001F500" : "⚠️")} Switch: {switchName}",
                         63 - switchSuffix.Length) + switchSuffix,
        };

        int vmsWithIp = 0;
        foreach (var name in vmNames)
        {
            if (_vm!.GetCachedVmIp(name) is { } ip)
            {
                lines.Add(TruncateLine($"\U0001F4E6 {name}: {ip}", 63));   // 📦 box (VM) — distinct from the 🖥 app row; one row per VM with a known IP
                vmsWithIp++;
            }
        }
        // Diagnostic breadcrumb: a configured VM with no cached IP is silently omitted from the
        // tooltip above (by design — there's no "IP unknown" placeholder), which makes a report
        // like "the VM is running but its IP never shows" otherwise unfalsifiable from the log
        // alone. This line gives a next occurrence something concrete to compare against
        // VmService's own state/summary logging.
        if (vmNames.Count > 0 && vmsWithIp < vmNames.Count)
            _loggerFactory?.CreateLogger("App").LogDebug(
                "Tooltip: {WithIp}/{Total} configured VM(s) have a cached guest IP (switch: {Switch})",
                vmsWithIp, vmNames.Count, switchName);

        return ClampTooltip(string.Join("\n", lines));
    }

    /// <summary>
    /// Enforces the Win32 balloon-tip 127-UTF-16-char hard limit without severing a surrogate pair
    /// (the emoji rows are 2 UTF-16 code units each) — a bare index-cut can corrupt the tail glyph.
    /// </summary>
    private static string ClampTooltip(string tooltip)
    {
        const int maxTipLength = 127;
        if (tooltip.Length <= maxTipLength) return tooltip;
        int cut = maxTipLength - 1;   // leave room for the ellipsis
        if (char.IsHighSurrogate(tooltip[cut - 1])) cut--;
        return string.Concat(tooltip.AsSpan(0, cut), "…");
    }

    /// <summary>
    /// Slow safety-net refresh, retained after issue #16's move to an event-driven tooltip. VM state
    /// changes are now pushed by <see cref="VmService.SubscribeStateWatcher"/>; this loop exists only
    /// for the input no cheap Hyper-V event can push — a guest DHCP <b>IP</b> that appears or changes
    /// with no VM state transition (Msvm_ComputerSystem raises no event for it). Each tick just
    /// refreshes VmService's caches; the resulting <see cref="VmService.StatusesChanged"/> re-posts
    /// the tooltip via <see cref="OnVmStatuses"/>. Kept at 60 s so IP-appearance latency is no worse
    /// than before; it could be lengthened to trim idle cost at the price of slower IP appearance.
    /// Runs until the process exits; never throws (RefreshOnceAsync swallows its own failures).
    /// </summary>
    private async Task TooltipRefreshLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        while (await timer.WaitForNextTickAsync())
            if (_vm is not null) await _vm.RefreshOnceAsync();
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

    /// <summary>Swaps the tray icon to the given state, disposing the previous one. The state is decided
    /// by <see cref="NetworkStatusUi.IconFor"/> from the apply OUTCOME — never inferred here.</summary>
    private void SetTrayIcon(TrayIconState state)
    {
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
