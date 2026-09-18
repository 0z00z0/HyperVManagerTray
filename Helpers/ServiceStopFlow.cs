using HyperVManagerTray.Models;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// The whole stop of a Hyper-V service, from the first check to the outcome (issue #114). Every side
/// effect arrives as a delegate, so the rule that matters — the service is never stopped while a managed
/// VM might be running — is testable without a host, a service or a dialog.
///
/// <para>The guard runs twice when VMs are saved first: once before asking, and again on a fresh read
/// after the saves, because a VM can be started by something else while the saves run.</para>
/// </summary>
public static class ServiceStopFlow
{
    public enum Outcome
    {
        Stopped,
        /// <summary>The person said no to a prompt.</summary>
        Declined,
        /// <summary>The guard refused; nothing was saved or stopped.</summary>
        Refused,
        /// <summary>Saving a VM failed, so the service was left running.</summary>
        SaveFailed,
        /// <summary>The stop itself was attempted and failed.</summary>
        StopFailed,
    }

    /// <param name="Outcome">What happened.</param>
    /// <param name="Message">A sentence for the person, or null when there is nothing to report (a decline).</param>
    public sealed record Result(Outcome Outcome, string? Message);

    /// <summary>A fresh read of the VM states, as <see cref="ServiceStopGuard.Evaluate"/> takes them.</summary>
    public sealed record VmRead(IReadOnlyList<VmStatus>? Statuses, bool StatesKnown);

    /// <param name="kind">The service to stop.</param>
    /// <param name="managedVms">Names of the VMs in config.</param>
    /// <param name="read">Reads the VM states now.</param>
    /// <param name="confirm">Shows a yes/no prompt; true is yes.</param>
    /// <param name="saveAll">Saves the named VMs and returns null when every one reached Saved, or a
    /// sentence naming what failed.</param>
    /// <param name="stop">Stops the service and returns null on success, or the reason it failed.</param>
    public static async Task<Result> RunAsync(
        HyperVServiceKind kind,
        IReadOnlyList<string> managedVms,
        Func<Task<VmRead>> read,
        Func<string, bool> confirm,
        Func<IReadOnlyList<string>, Task<string?>> saveAll,
        Func<Task<string?>> stop)
    {
        var first    = await read().ConfigureAwait(true);
        var decision = ServiceStopGuard.Evaluate(kind, managedVms, first.Statuses, first.StatesKnown);

        switch (decision.Verdict)
        {
            case ServiceStopGuard.Verdict.RefusedStatesUnknown:
                return new Result(Outcome.Refused, ServiceStopGuard.StatesUnknownMessage(kind));

            case ServiceStopGuard.Verdict.RefusedBusy:
                return new Result(Outcome.Refused, ServiceStopGuard.BusyMessage(kind, decision.BusyVms));

            case ServiceStopGuard.Verdict.ConfirmSideEffects:
                if (!confirm(ServiceStopGuard.ConfirmStopPrompt(kind))) return new Result(Outcome.Declined, null);
                break;

            case ServiceStopGuard.Verdict.OfferSaveFirst:
                if (!confirm(ServiceStopGuard.SaveFirstPrompt(kind, decision.VmsToSave)))
                    return new Result(Outcome.Declined, null);

                if (await saveAll(decision.VmsToSave).ConfigureAwait(true) is { } saveError)
                    return new Result(Outcome.SaveFailed,
                        $"{HyperVServiceNames.DisplayName(kind)} was left running: {saveError}");

                // A VM may have been started while the saves ran; only a fresh read may clear the stop.
                var again   = await read().ConfigureAwait(true);
                var recheck = ServiceStopGuard.Evaluate(kind, managedVms, again.Statuses, again.StatesKnown);
                if (recheck.Verdict is not (ServiceStopGuard.Verdict.Allowed or ServiceStopGuard.Verdict.ConfirmSideEffects))
                    return new Result(Outcome.Refused,
                        $"{HyperVServiceNames.DisplayName(kind)} was left running: a VM was still running after the save.");
                break;

            case ServiceStopGuard.Verdict.Allowed:
                break;

            default:
                return new Result(Outcome.Refused, $"{HyperVServiceNames.DisplayName(kind)} was not stopped.");
        }

        if (await stop().ConfigureAwait(true) is { } stopError)
            return new Result(Outcome.StopFailed, $"{HyperVServiceNames.DisplayName(kind)} could not be stopped: {stopError}");

        return new Result(Outcome.Stopped, $"{HyperVServiceNames.DisplayName(kind)} is stopped.");
    }
}
