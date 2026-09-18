using Microsoft.Extensions.Logging;
using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using HyperVManagerTray.Services;

namespace HyperVManagerTray.UI;

/// <summary>
/// The dashboard's side of starting and stopping the Hyper-V services (issue #114): prompts and tray
/// balloons around <see cref="HyperVServiceControl"/>, which network rules and MQTT share.
/// </summary>
internal sealed class HyperVServiceActions
{
    private readonly HyperVServiceControl _control;
    // The tray balloon (title, message, isError), not suppressed by a visible dashboard: it answers a
    // button that was just clicked there.
    private readonly Action<string, string, bool> _notify;

    private static string Title => $"{AppInfo.Name} — Hyper-V services";

    public HyperVServiceActions(HyperVServiceControl control, Action<string, string, bool> notify)
    {
        _control = control;
        _notify  = notify;
    }

    /// <summary>Starts <paramref name="kind"/> and reports a failure. Returns true when it runs.</summary>
    public async Task<bool> StartAsync(HyperVServiceKind kind, VmOpOrigin origin)
    {
        UiActivityLog.Logger.LogInformation("Services: start '{Service}' (origin={Origin})",
            HyperVServiceNames.DisplayName(kind), origin);
        var error = await _control.StartAsync(kind, origin).ConfigureAwait(true);
        if (error is null) return true;

        _notify(Title, $"{HyperVServiceNames.DisplayName(kind)} could not be started: {error}", true);
        return false;
    }

    /// <summary>
    /// Stops <paramref name="kind"/>: refused while any VM on the host is running, with the offer to save
    /// them all first. Prompts are modal; the outcome goes to the tray balloon.
    /// </summary>
    public async Task<ServiceStopFlow.Result> StopAsync(HyperVServiceKind kind, VmOpOrigin origin)
    {
        UiActivityLog.Logger.LogInformation("Services: stop '{Service}' requested (origin={Origin})",
            HyperVServiceNames.DisplayName(kind), origin);

        var result = await _control.StopAsync(kind, origin, "asked for on the dashboard",
            confirm: prompt => NativeMethods.Confirm(prompt, Title)).ConfigureAwait(true);

        UiActivityLog.Logger.LogInformation("Services: stop '{Service}' — {Outcome}{Detail}",
            HyperVServiceNames.DisplayName(kind), result.Outcome,
            result.Message is null ? "" : $": {result.Message}");

        if (result.Message is { } message)
            _notify(Title, message, result.Outcome != ServiceStopFlow.Outcome.Stopped);
        return result;
    }

    /// <summary>
    /// The VM card's Start while a service is down: starts each stopped service, waits until the VMs can
    /// be read, then starts the VM. Reports whichever step failed and goes no further.
    /// </summary>
    public async Task StartServicesThenVmAsync(string vmName, VmOpOrigin origin)
    {
        UiActivityLog.Logger.LogInformation("Services: start services, then '{Vm}' (origin={Origin})", vmName, origin);

        if (await _control.StartServicesThenVmAsync(vmName, origin).ConfigureAwait(true) is { } error)
            _notify(Title, $"{error} Try Start again in a moment.", true);
    }
}
