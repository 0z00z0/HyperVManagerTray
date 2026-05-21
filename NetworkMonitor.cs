using System.Net.NetworkInformation;
using HyperVNetworkSwitcher.Models;
using Microsoft.Extensions.Logging;

namespace HyperVNetworkSwitcher;

public sealed class NetworkMonitor : IDisposable
{
    private readonly ConfigManager _config;
    private readonly HyperVManager _hyperV;
    private readonly ILogger<NetworkMonitor> _logger;
    private readonly System.Threading.Timer _debounceTimer;
    private MatchResult? _lastApplied;
    // Tracks which physical adapter name was last passed to Set-VMSwitch so we can skip
    // redundant re-binds (which cause a brief VM network drop) when nothing has changed.
    private string? _lastBoundAdapterInterface;

    public event EventHandler<MatchResult>? SwitchApplied;

    public MatchResult? LastApplied => _lastApplied;

    public NetworkMonitor(ConfigManager config, HyperVManager hyperV, ILogger<NetworkMonitor> logger)
    {
        _config = config;
        _hyperV = hyperV;
        _logger = logger;
        _debounceTimer = new System.Threading.Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);

        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        _config.ConfigReloaded += (_, _) => Schedule();
    }

    public void Start() => Schedule();

    private void OnNetworkChanged(object? sender, EventArgs e) =>
        _debounceTimer.Change(1500, Timeout.Infinite);

    private void Schedule() =>
        _debounceTimer.Change(0, Timeout.Infinite);

    private async void OnDebounceElapsed(object? _)
    {
        try
        {
            var result = AdapterMatcher.Evaluate(_config.Current);
            _logger.LogInformation("Evaluated: rule='{Rule}' switch='{Switch}'", result.RuleName, result.VirtualSwitch);

            if (_lastApplied?.VirtualSwitch == result.VirtualSwitch &&
                _lastApplied?.TargetVms.SequenceEqual(result.TargetVms) == true)
            {
                _logger.LogDebug("No switch change needed");
                return;
            }

            await ApplyAsync(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during network evaluation");
        }
    }

    public async Task ForceEvaluateAsync()
    {
        var result = AdapterMatcher.Evaluate(_config.Current);
        await ApplyAsync(result);
    }

    public async Task ManualOverrideAsync(string vmName, string switchName)
    {
        _logger.LogInformation("Manual override: {Vm} → {Switch}", vmName, switchName);
        var vm = _config.Current.VirtualMachines.FirstOrDefault(v => v.Name == vmName);
        if (vm is null) return;

        await _hyperV.ApplySwitchAsync(vmName, vm.NicName, switchName);

        // Manual override bypasses the binding logic; force a re-bind next time a rule fires.
        _lastBoundAdapterInterface = null;

        var result = new MatchResult($"Manual ({switchName})", switchName, [vmName]);
        _lastApplied = result;
        SwitchApplied?.Invoke(this, result);
    }

    private async Task ApplyAsync(MatchResult result)
    {
        // When a specific rule matched, re-bind the Hyper-V virtual switch to the detected
        // physical adapter before connecting any VMs.  This is what makes an "Internal"
        // switch become an "External" (bridged) switch pointing at the right LAN NIC.
        // Skip for fallback — the fallback switch (Default Switch / NAT) needs no binding.
        //
        // Only call Set-VMSwitch when the physical adapter actually changed: repeated calls
        // with the same adapter cause a brief VM network drop even if nothing changed.
        // Reset _lastBoundAdapterInterface when falling back so the next rule-match
        // always re-binds (the switch may have been left on a different adapter).
        if (result.RuleName == "Fallback")
        {
            _lastBoundAdapterInterface = null;
        }
        else if (result.HostAdapterInterfaceName != "—" &&
                 result.HostAdapterInterfaceName != _lastBoundAdapterInterface)
        {
            await _hyperV.UpdateSwitchBindingAsync(result.VirtualSwitch, result.HostAdapterInterfaceName);
            _lastBoundAdapterInterface = result.HostAdapterInterfaceName;
        }

        foreach (var vmName in result.TargetVms)
        {
            var vm = _config.Current.VirtualMachines.FirstOrDefault(v => v.Name == vmName);
            if (vm is null)
            {
                _logger.LogWarning("VM '{Vm}' not found in config", vmName);
                continue;
            }
            await _hyperV.ApplySwitchAsync(vmName, vm.NicName, result.VirtualSwitch);
        }

        _lastApplied = result;
        SwitchApplied?.Invoke(this, result);
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        _debounceTimer.Dispose();
    }
}
