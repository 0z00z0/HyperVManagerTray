using HyperVManagerTray.Models;
using ZeroZero.Mqtt;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// What an inbound MQTT command is allowed to do (issue #75). Every VM verb passes through
/// <see cref="VmStateUi.AllowedVerbs"/> — the same gate the dashboard's buttons use — so a remote write
/// can reach nothing the dashboard cannot, and a disallowed verb is refused rather than attempted.
///
/// <para>The state refusals are the application's own sentences, carried verbatim to
/// <see cref="MqttConnectionSetup.CommandRefused"/>: only this app knows why a verb it understands is one
/// the VM's state does not allow. A value outside a select's options never reaches this gate; the module
/// refuses it first, in its own words.</para>
/// </summary>
public static class MqttCommandGate
{
    /// <summary>The power verbs announced as select options, in the order the receiver shows them.</summary>
    public static readonly IReadOnlyList<VmOpKind> PowerVerbs =
        [VmOpKind.Start, VmOpKind.Shutdown, VmOpKind.Pause, VmOpKind.Save, VmOpKind.Resume];

    /// <summary>The option strings for the per-VM power select.</summary>
    public static IReadOnlyList<string> PowerOptions => [.. PowerVerbs.Select(v => v.ToString())];

    /// <summary>Whether <paramref name="kind"/> may be requested for a VM currently in
    /// <paramref name="state"/>, and what to run when it may.</summary>
    public static MqttCommandVerdict Power(string? state, VmOpKind kind, Func<CancellationToken, Task> run) =>
        VmStateUi.AllowedVerbs(state).Contains(kind)
            ? MqttCommandVerdict.Accept(run)
            : MqttCommandVerdict.Refuse($"'{kind}' is not available while the VM is {Describe(state)}.");

    /// <summary>The verb an on/off switch means, given the VM's current state: on starts a stopped or
    /// saved VM, off shuts a running one down. A state that allows neither refuses.</summary>
    public static MqttCommandVerdict Running(
        string? state, bool on, Func<VmOpKind, CancellationToken, Task> run)
    {
        var kind = on ? VmOpKind.Start : VmOpKind.Shutdown;
        // A paused VM turned on resumes rather than starts — Start is not among its allowed verbs.
        if (on && VmStateUi.ClassifyShape(state) == VmStateUi.Shape.Paused) kind = VmOpKind.Resume;
        return Power(state, kind, ct => run(kind, ct));
    }

    /// <summary>
    /// A VM verb while a Hyper-V service is down (issue #114). Only a start is offered, as on the
    /// dashboard, and it starts the services first: with vmms down any VM may be started, since its state
    /// cannot be read; with only the Host Compute Service down, one that is off, saved or paused.
    /// </summary>
    public static MqttCommandVerdict PowerWhileServicesDown(
        string? state, bool vmmsDown, VmOpKind kind, Func<CancellationToken, Task> startViaServices)
    {
        bool startable = vmmsDown
            || VmStateUi.ClassifyShape(state) is VmStateUi.Shape.Off or VmStateUi.Shape.Saved or VmStateUi.Shape.Paused;
        return (kind is VmOpKind.Start or VmOpKind.Resume) && startable
            ? MqttCommandVerdict.Accept(startViaServices)
            : MqttCommandVerdict.Refuse(
                $"'{kind}' is not available while a Hyper-V service is stopped. Start is, and starts the service first.");
    }

    /// <summary>The verbs a Hyper-V service takes, as select options and button suffixes.</summary>
    public static readonly IReadOnlyList<string> ServiceOptions = ["Start", "Stop"];

    /// <summary>The verbs <paramref name="kind"/> offers over MQTT: the Host Compute Service has no stop,
    /// because it is stopped only from the dashboard.</summary>
    public static IReadOnlyList<string> ServiceOptionsFor(HyperVServiceKind kind) =>
        ServiceStopGuard.MayStopUnattended(kind) ? ServiceOptions : [ServiceOptions[0]];

    /// <summary>
    /// Whether a service start or stop may be requested now (issue #114): a start only from stopped, a
    /// stop only from running, and never a stop of the Host Compute Service. A stop accepted here still
    /// passes the stop guard, which saves every running VM first or refuses — that needs a fresh VM read,
    /// so its answer arrives in the log, not here.
    /// </summary>
    public static MqttCommandVerdict Service(
        HyperVServiceKind kind, HyperVServiceState state, bool start, Func<CancellationToken, Task> run)
    {
        if (!start && !ServiceStopGuard.MayStopUnattended(kind))
            return MqttCommandVerdict.Refuse(ServiceStopGuard.UnattendedStopRefusedMessage(kind));

        var needed = start ? HyperVServiceState.Stopped : HyperVServiceState.Running;
        return state == needed
            ? MqttCommandVerdict.Accept(run)
            : MqttCommandVerdict.Refuse(
                $"'{(start ? "Start" : "Stop")}' is not available while the service is "
                + $"{HyperVServiceNames.StateText(state).ToLowerInvariant()}.");
    }

    /// <summary>The VM's state as a refusal names it.</summary>
    private static string Describe(string? state) =>
        string.IsNullOrWhiteSpace(state) ? "in an unknown state" : state;
}
