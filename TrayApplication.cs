using HyperVNetworkSwitcher.Models;
using Microsoft.Extensions.Logging;

namespace HyperVNetworkSwitcher;

/// <summary>
/// The system-tray UI and application context.  Owns the <see cref="NotifyIcon"/>, its
/// context menu, and the status popup; wires user actions (force re-evaluate, manual
/// override, add current network, open config/log, toggle startup, exit) to the underlying
/// services; and updates the icon colour and popup whenever a switch change is applied.
/// </summary>
public sealed class TrayApplication : ApplicationContext, IDisposable
{
    private const string AppName = "HyperVNetworkSwitcher";

    private readonly ConfigManager  _config;
    private readonly NetworkMonitor _monitor;
    private readonly HyperVManager  _hyperV;
    private readonly ILogger<TrayApplication> _logger;
    private readonly SynchronizationContext   _uiContext;
    private readonly StartupManager    _startup = new();
    private readonly NotifyIcon        _trayIcon;
    private readonly StatusPopupForm   _popup;
    private readonly ToolStripMenuItem _overrideMenu;
    private readonly ToolStripMenuItem _startupItem;
    private Icon? _currentIcon;

    // ── Constructor ───────────────────────────────────────────────────────────

    public TrayApplication(
        ConfigManager  config,
        NetworkMonitor monitor,
        HyperVManager  hyperV,
        ILogger<TrayApplication> logger)
    {
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();
        _config    = config;
        _monitor   = monitor;
        _hyperV    = hyperV;
        _logger    = logger;

        _popup        = new StatusPopupForm();
        _overrideMenu = new ToolStripMenuItem("Manual Override");
        _startupItem  = new ToolStripMenuItem("Run on startup", null, OnToggleStartup)
        {
            Checked = _startup.IsEnabled
        };

        RebuildOverrideMenu();

        _currentIcon = TrayIconBuilder.Build(bridged: false);
        _trayIcon = new NotifyIcon
        {
            Icon             = _currentIcon,
            Text             = AppName,
            ContextMenuStrip = BuildContextMenu(),
            Visible          = true
        };

        // Left-click toggles the status popup
        _trayIcon.MouseClick += OnTrayIconClick;

        _monitor.SwitchApplied += OnSwitchApplied;
        _config.ConfigReloaded += (_, _) => RebuildOverrideMenu();

        // Pre-populate the popup with the current network state so it has content on the
        // very first click, without waiting for PowerShell commands to complete.
        // This runs on the constructor thread (before Application.Run), which is correct —
        // the WinForms sync context is not set up yet, so posting via _uiContext is unreliable
        // at this stage.  The popup fields are plain strings; the first paint reads them when
        // ShowNearTray() is called after the message loop starts.
        try
        {
            var initial = AdapterMatcher.Evaluate(_config.Current);
            _popup.Update(initial);
        }
        catch { /* non-fatal; full evaluation follows once the monitor starts */ }
    }

    // ── Network switch events ─────────────────────────────────────────────────

    private void OnSwitchApplied(object? sender, MatchResult result)
    {
        _uiContext.Post(_ => ApplyStatusUpdate(result), null);

        // Fetch VM IPs in background — VM needs a moment after switch to get a DHCP lease
        _ = RefreshVmIpAsync(result.TargetVms, delayMs: 3000);
    }

    private void ApplyStatusUpdate(MatchResult result)
    {
        _popup.Update(result);

        _trayIcon.Text = $"{AppName}: {result.VirtualSwitch}";

        var bridged = result.VirtualSwitch != _config.Current.Fallback.VirtualSwitch;
        var oldIcon  = _currentIcon;
        _currentIcon     = TrayIconBuilder.Build(bridged);
        _trayIcon.Icon   = _currentIcon;
        oldIcon?.Dispose();

        var summary = $"{string.Join(", ", result.TargetVms)} → {result.VirtualSwitch}  ({result.RuleName})";
        _trayIcon.BalloonTipTitle = AppName;
        _trayIcon.BalloonTipText  = summary;
        _trayIcon.ShowBalloonTip(4000);
    }

    private async Task RefreshVmIpAsync(IReadOnlyList<string> targetVms, int delayMs = 0)
    {
        try
        {
            if (delayMs > 0) await Task.Delay(delayMs);

            var parts = new List<string>();
            foreach (var vm in targetVms)
            {
                var ips = await _hyperV.GetVmIpAddressesAsync(vm);
                parts.Add(ips.Length > 0
                    ? string.Join(", ", ips)
                    : "no IP");
            }
            _uiContext.Post(_ => _popup.SetVmIp(string.Join(" | ", parts)), null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VM IP refresh failed");
            _uiContext.Post(_ => _popup.SetVmIp("—"), null);
        }
    }

    // ── Menu helpers ──────────────────────────────────────────────────────────

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Force Re-evaluate", null, async (_, _) => await _monitor.ForceEvaluateAsync());
        menu.Items.Add(_overrideMenu);
        menu.Items.Add("Add current network as bridged", null, OnAddCurrentAsBridged);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open config.json", null, OnOpenConfig);
        menu.Items.Add("Open log file",    null, OnOpenLogFile);
        menu.Items.Add("Reload config",    null, OnReloadConfig);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, OnExit);
        return menu;
    }

    private void RebuildOverrideMenu()
    {
        _overrideMenu.DropDownItems.Clear();
        foreach (var vm in _config.Current.VirtualMachines)
        {
            var switches = new HashSet<string> { _config.Current.Fallback.VirtualSwitch };
            foreach (var rule in _config.Current.Rules) switches.Add(rule.VirtualSwitch);

            foreach (var sw in switches.Order())
            {
                var vmCapture = vm.Name;
                var swCapture = sw;
                _overrideMenu.DropDownItems.Add(new ToolStripMenuItem(
                    $"{vm.Name} → {sw}", null,
                    async (_, _) => await _monitor.ManualOverrideAsync(vmCapture, swCapture)));
            }
        }
    }

    // ── Menu item handlers ────────────────────────────────────────────────────

    private async void OnAddCurrentAsBridged(object? sender, EventArgs e)
    {
        var info = AdapterMatcher.GetCurrentNetworkInfo();
        if (info is null)
        {
            MessageBox.Show(
                "No active network adapter with an IPv4 address was found.",
                AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var normNew   = AdapterMatcher.NormalizeMac(info.Mac);
        var duplicate = _config.Current.Rules.FirstOrDefault(r =>
            r.Conditions.AdapterMac is not null &&
            AdapterMatcher.NormalizeMac(r.Conditions.AdapterMac) == normNew);

        if (duplicate is not null)
        {
            MessageBox.Show(
                $"This adapter is already covered by rule \"{duplicate.Name}\".\n\nEdit config.json to update it.",
                AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var fallbackSwitch = _config.Current.Fallback.VirtualSwitch;
        var bridgedSwitch  = _config.Current.Rules
            .Select(r => r.VirtualSwitch)
            .Where(s => s != fallbackSwitch)
            .OrderBy(s => s.Contains("bridge", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault() ?? "Bridged";

        var confirm = MessageBox.Show(
            $"Add the following rule to config.json?\n\n" +
            $"  Adapter :  {info.AdapterDescription}\n" +
            $"  MAC     :  {info.Mac}\n" +
            $"  Network :  {info.IpCidr}\n" +
            $"  Switch  :  {bridgedSwitch}",
            "Add Current Network as Bridged",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (confirm != DialogResult.Yes) return;

        var rule = new NetworkRule
        {
            Name          = info.AdapterDescription.Length > 40
                                ? info.AdapterDescription[..40].TrimEnd()
                                : info.AdapterDescription,
            Priority      = _config.Current.Rules.Count > 0
                                ? _config.Current.Rules.Max(r => r.Priority) + 10
                                : 10,
            Conditions    = new RuleConditions { AdapterMac = info.Mac, IpCidr = info.IpCidr },
            VirtualSwitch = bridgedSwitch,
            TargetVms     = _config.Current.Fallback.TargetVms.ToList()
        };

        try
        {
            _config.AddBridgedRule(rule);
            await _monitor.ForceEvaluateAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save rule:\n{ex.Message}",
                AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnTrayIconClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (_popup.Visible) _popup.Hide();
        else                _popup.ShowNearTray();
    }

    private void OnOpenLogFile(object? sender, EventArgs e)
    {
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HyperVNetworkSwitcher", "switcher.log");

        if (!File.Exists(logPath))
        {
            MessageBox.Show($"No log file found yet.\n\n{logPath}",
                AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(logPath) { UseShellExecute = true });
        }
        catch
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{logPath}\"");
        }
    }

    private void OnOpenConfig(object? sender, EventArgs e)
    {
        var path = ConfigManager.GetConfigPath();
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
    }

    private void OnReloadConfig(object? sender, EventArgs e)
    {
        _config.Load();
        _trayIcon.BalloonTipTitle = AppName;
        _trayIcon.BalloonTipText  = "Config reloaded.";
        _trayIcon.ShowBalloonTip(2000);
    }

    private void OnToggleStartup(object? sender, EventArgs e)
    {
        try
        {
            if (_startup.IsEnabled)
            {
                _startup.Disable();
                _startupItem.Checked = false;
            }
            else
            {
                _startup.Enable(Environment.ProcessPath
                    ?? throw new InvalidOperationException("Cannot determine executable path."));
                _startupItem.Checked = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to toggle startup scheduled task");
            MessageBox.Show($"Could not change the startup setting:\n\n{ex.Message}",
                AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OnExit(object? sender, EventArgs e)
    {
        _trayIcon.Visible = false;
        Application.Exit();
    }

    // ── Disposal ──────────────────────────────────────────────────────────────

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _popup.Dispose();
            _trayIcon.Dispose();
            _currentIcon?.Dispose();
        }
        base.Dispose(disposing);
    }

}
