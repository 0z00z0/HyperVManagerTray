using Microsoft.Extensions.Logging;
using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;

namespace HyperVManagerTray.Services;

/// <summary>
/// Starting and stopping the Hyper-V services for every caller — the dashboard, network rules and MQTT
/// (issue #114). Holds no UI: prompts arrive as a delegate, and the dashboard's balloons stay in
/// <c>UI\HyperVServiceActions</c>.
///
/// <para>Every stop goes through <see cref="ServiceStopFlow"/>, so the refusal while any VM runs and the
/// save-first step live in one tested place. Every start and stop is written to vm-power.log with its
/// origin by <see cref="HyperVServiceMonitor"/>; the lines here add why it was asked for.</para>
/// </summary>
public sealed class HyperVServiceControl
{
    // A large-memory VM can take minutes to write its state to disk.
    private static readonly TimeSpan SaveTimeout = TimeSpan.FromMinutes(5);
    // vmms reports running a little before its WMI provider answers.
    private static readonly TimeSpan StatesTimeout = TimeSpan.FromSeconds(60);

    private readonly VmService            _vm;
    private readonly HyperVServiceMonitor _services;
    private readonly ILogger              _powerLog;

    public HyperVServiceControl(VmService vm, HyperVServiceMonitor services, ILogger powerLog)
    {
        _vm       = vm;
        _services = services;
        _powerLog = powerLog;
    }

    /// <summary>The service watcher, for callers that show or publish the states.</summary>
    public HyperVServiceMonitor Monitor => _services;

    /// <summary>Whether any service a VM start needs is not running. Unknown (not read yet) is not down.</summary>
    public bool AnyServiceDown => HyperVServiceNames.All.Any(k => HyperVServiceNames.IsDown(k, _services.State(k)));

    /// <summary>Whether vmms is down, in which case no VM state can be read.</summary>
    public bool VmmsDown => HyperVServiceNames.IsDown(
        HyperVServiceKind.VirtualMachineManagement, _services.State(HyperVServiceKind.VirtualMachineManagement));

    /// <summary>Starts <paramref name="kind"/>. Null when it runs, else the reason it did not. Never throws.</summary>
    public Task<string?> StartAsync(HyperVServiceKind kind, VmOpOrigin origin) =>
        _services.StartServiceAsync(kind, origin);

    /// <summary>
    /// Starts every service that is not running, vmms first, and waits until the VMs can be read. What
    /// runs before any VM start, from any source. Null when everything is ready, else a sentence saying
    /// what is not. Never throws.
    /// </summary>
    /// <param name="why">Why the services are needed, for the log.</param>
    public async Task<string?> EnsureRunningForVmStartAsync(VmOpOrigin origin, string why)
    {
        bool startedAny = false;
        // vmms first: it is what the VM start goes through, and it starts the Host Compute Service on
        // demand itself, so the second start is usually already satisfied.
        foreach (var kind in HyperVServiceNames.All)
        {
            if (!HyperVServiceNames.IsDown(kind, _services.State(kind))) continue;
            _powerLog.LogInformation("Service '{Service}' is {State}; starting it first ({Why}, origin={Origin})",
                HyperVServiceNames.DisplayName(kind), _services.State(kind), why, origin);
            if (await StartAsync(kind, origin).ConfigureAwait(true) is { } error)
                return $"{HyperVServiceNames.DisplayName(kind)} could not be started: {error}";
            startedAny = true;
        }

        if (startedAny && !await _vm.WaitForStatesAsync(StatesTimeout).ConfigureAwait(true))
            return "The services are running, but the VMs could not be read yet.";
        return null;
    }

    /// <summary>
    /// Stops <paramref name="kind"/>, refused while a VM is mid-transition or unreadable. With
    /// <paramref name="confirm"/> a person is asked before running VMs are saved; without it (a network
    /// rule or an MQTT command) every running VM is saved first and nobody is asked. Never throws.
    /// </summary>
    /// <param name="why">Why the stop was asked for, for the log.</param>
    public async Task<ServiceStopFlow.Result> StopAsync(
        HyperVServiceKind kind, VmOpOrigin origin, string why, Func<string, bool>? confirm)
    {
        var name = HyperVServiceNames.DisplayName(kind);
        _powerLog.LogInformation("Stop service '{Service}' requested ({Why}, origin={Origin}{Mode})",
            name, why, origin, confirm is null ? ", unattended: running VMs are saved first" : "");

        ServiceStopFlow.Result result;
        try
        {
            result = confirm is null
                ? await ServiceStopFlow.RunUnattendedAsync(
                      kind,
                      read:    ReadFreshAsync,
                      saveAll: names => SaveAllAsync(names, origin),
                      stop:    () => _services.StopServiceAsync(kind, origin)).ConfigureAwait(true)
                : await ServiceStopFlow.RunAsync(
                      kind,
                      read:    ReadFreshAsync,
                      confirm: confirm,
                      saveAll: names => SaveAllAsync(names, origin),
                      stop:    () => _services.StopServiceAsync(kind, origin)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _powerLog.LogWarning(ex, "Stop service '{Service}' failed ({Why}, origin={Origin})", name, why, origin);
            return new ServiceStopFlow.Result(ServiceStopFlow.Outcome.StopFailed, $"{name} could not be stopped: {ex.Message}");
        }

        _powerLog.Log(result.Outcome == ServiceStopFlow.Outcome.Stopped ? LogLevel.Information : LogLevel.Warning,
            "Stop service '{Service}' — {Outcome} ({Why}, origin={Origin}){Detail}",
            name, result.Outcome, why, origin, result.Message is null ? "" : $": {result.Message}");
        return result;
    }

    /// <summary>
    /// A VM start from any source while a service is down: the services first, then the VM. Null when the
    /// VM start was requested (its outcome follows on VmService.OperationProgress), else what stopped it.
    /// </summary>
    public async Task<string?> StartServicesThenVmAsync(string vmName, VmOpOrigin origin)
    {
        if (await EnsureRunningForVmStartAsync(origin, $"VM '{vmName}' is to start").ConfigureAwait(true) is { } error)
        {
            _powerLog.LogWarning("Start '{Vm}' not requested (origin={Origin}): {Error}", vmName, origin, error);
            return $"{error} {vmName} was not started.";
        }

        _vm.BeginPowerAction(vmName, VmOpKind.Start, origin);
        return null;
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
        _powerLog.LogInformation("Saving before a service stop (origin={Origin}): {Vms}", origin, string.Join(", ", names));

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
}
