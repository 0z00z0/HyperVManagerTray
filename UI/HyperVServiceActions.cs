using Microsoft.Extensions.Logging;
using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using HyperVManagerTray.Services;

namespace HyperVManagerTray.UI;

/// <summary>
/// Starting and stopping the Hyper-V services by hand, and starting a VM whose services are down
/// (issue #114). The dashboard is the only caller today; network rules and MQTT reach the same methods
/// in a later release, which is why the origin is a parameter.
///
/// <para>Every stop goes through <see cref="ServiceStopFlow"/>, so the refusal while a managed VM runs,
/// the offer to save first and the Host Compute Service confirmation live in one tested place.</para>
/// </summary>
internal sealed class HyperVServiceActions
{
    // A large-memory VM can take minutes to write its state to disk.
    private static readonly TimeSpan SaveTimeout = TimeSpan.FromMinutes(5);
    // vmms reports running a little before its WMI provider answers.
    private static readonly TimeSpan StatesTimeout = TimeSpan.FromSeconds(60);

    private readonly ConfigManager         _config;
    private readonly VmService             _vm;
    private readonly HyperVServiceMonitor  _services;
    // The tray balloon (title, message, isError), not suppressed by a visible dashboard: it answers a
    // button that was just clicked there.
    private readonly Action<string, string, bool> _notify;

    private static string Title => $"{AppInfo.Name} — Hyper-V services";

    public HyperVServiceActions(ConfigManager config, VmService vm, HyperVServiceMonitor services,
                                Action<string, string, bool> notify)
    {
        _config   = config;
        _vm       = vm;
        _services = services;
        _notify   = notify;
    }

    /// <summary>Starts <paramref name="kind"/> and reports a failure. Returns true when it runs.</summary>
    public async Task<bool> StartAsync(HyperVServiceKind kind, VmOpOrigin origin)
    {
        UiActivityLog.Logger.LogInformation("Services: start '{Service}' (origin={Origin})",
            HyperVServiceNames.DisplayName(kind), origin);
        var error = await _services.StartServiceAsync(kind, origin).ConfigureAwait(true);
        if (error is null) return true;

        _notify(Title, $"{HyperVServiceNames.DisplayName(kind)} could not be started: {error}", true);
        return false;
    }

    /// <summary>
    /// Stops <paramref name="kind"/> through <see cref="ServiceStopFlow"/>: refused while a managed VM is
    /// running, with the offer to save first. Prompts are modal; the outcome goes to the tray balloon.
    /// </summary>
    public async Task<ServiceStopFlow.Result> StopAsync(HyperVServiceKind kind, VmOpOrigin origin)
    {
        UiActivityLog.Logger.LogInformation("Services: stop '{Service}' requested (origin={Origin})",
            HyperVServiceNames.DisplayName(kind), origin);

        var managed = _config.Current.VirtualMachines.Select(v => v.Name).ToList();
        var result  = await ServiceStopFlow.RunAsync(
            kind, managed,
            read:    ReadFreshAsync,
            confirm: prompt => NativeMethods.Confirm(prompt, Title),
            saveAll: names => SaveAllAsync(names, origin),
            stop:    () => _services.StopServiceAsync(kind, origin)).ConfigureAwait(true);

        UiActivityLog.Logger.LogInformation("Services: stop '{Service}' — {Outcome}{Detail}",
            HyperVServiceNames.DisplayName(kind), result.Outcome,
            result.Message is null ? "" : $": {result.Message}");

        if (result.Message is { } message)
            _notify(Title, message, result.Outcome != ServiceStopFlow.Outcome.Stopped);
        return result;
    }

    /// <summary>
    /// A VM read taken after this call began. A read that fails leaves the previous list in place, so
    /// "known" also requires the list to be newer than the request — a guard must never clear a stop on
    /// a list that predates it.
    /// </summary>
    private async Task<ServiceStopFlow.VmRead> ReadFreshAsync()
    {
        var asked = DateTime.UtcNow;
        if (_vm.ServiceAvailable) await _vm.RefreshOnceAsync().ConfigureAwait(true);
        return new ServiceStopFlow.VmRead(_vm.GetCachedStatuses(), _vm.StatesKnown && _vm.LastReadUtc >= asked);
    }

    /// <summary>Saves every named VM and waits for each to read Saved. Null when all did, else what failed.</summary>
    private async Task<string?> SaveAllAsync(IReadOnlyList<string> names, VmOpOrigin origin)
    {
        // Waits first, actions second: a save that fails at once must not report before anything listens.
        var waits = names.Select(n => (Name: n, Wait: _vm.WaitUntilSavedAsync(n, SaveTimeout))).ToList();
        foreach (var name in names) _vm.BeginPowerAction(name, VmOpKind.Save, origin);

        await Task.WhenAll(waits.Select(w => w.Wait)).ConfigureAwait(true);

        var failed = waits.Where(w => w.Wait.Result != VmService.StartReadiness.Running).ToList();
        if (failed.Count == 0) return null;
        return string.Join(" ", failed.Select(w => w.Wait.Result == VmService.StartReadiness.Failed
            ? $"{w.Name} could not be saved."
            : $"{w.Name} did not finish saving in time."));
    }

    /// <summary>
    /// The VM card's Start while a service is down: starts each stopped service, waits until the VMs can
    /// be read, then starts the VM. Reports whichever step failed and goes no further.
    /// </summary>
    public async Task StartServicesThenVmAsync(string vmName, VmOpOrigin origin)
    {
        UiActivityLog.Logger.LogInformation("Services: start services, then '{Vm}' (origin={Origin})", vmName, origin);

        // vmms first: it is what the VM start goes through, and it starts the Host Compute Service on
        // demand itself, so the second start is usually already satisfied.
        foreach (var kind in HyperVServiceNames.All)
        {
            if (_services.State(kind) == HyperVServiceState.Running) continue;
            if (!await StartAsync(kind, origin).ConfigureAwait(true)) return;
        }

        if (!await _vm.WaitForStatesAsync(StatesTimeout).ConfigureAwait(true))
        {
            _notify(Title, $"The services are running, but the VMs could not be read yet, so {vmName} was not started. "
                           + "Try Start again in a moment.", true);
            return;
        }

        _vm.BeginPowerAction(vmName, VmOpKind.Start, origin);
    }
}
