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
/// the VM's state does not allow. A refusal reaches the log alone, so a verb that is not valid is also
/// declared unavailable — see <see cref="PowerAvailable"/> and <see cref="ServiceVerbAvailable"/>, which
/// the buttons' <c>Include</c> reads — and the refusal is the backstop rather than the only answer.</para>
/// </summary>
public static class MqttCommandGate
{
    /// <summary>The power verbs one button each is published for, in the order the receiver shows them.</summary>
    public static readonly IReadOnlyList<VmOpKind> PowerVerbs =
        [VmOpKind.Start, VmOpKind.Shutdown, VmOpKind.Pause, VmOpKind.Save, VmOpKind.Resume];

    /// <summary>Whether <paramref name="kind"/> is offered for this VM right now — what a power button's
    /// availability reads, so a button is greyed out exactly when pressing it would be refused.</summary>
    /// <remarks>Composed from the same two expressions the verdicts below are, rather than restating
    /// them: an availability that disagreed with the gate would grey out a verb that works, or offer one
    /// that cannot.</remarks>
    public static bool PowerAvailable(string? state, VmOpKind kind, bool anyServiceDown, bool vmmsDown) =>
        anyServiceDown
            ? StartableWhileServicesDown(state, vmmsDown, kind)
            : VmStateUi.AllowedVerbs(state).Contains(kind);

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
        string? state, bool vmmsDown, VmOpKind kind, Func<CancellationToken, Task> startViaServices) =>
        StartableWhileServicesDown(state, vmmsDown, kind)
            ? MqttCommandVerdict.Accept(startViaServices)
            : MqttCommandVerdict.Refuse(
                $"'{kind}' is not available while a Hyper-V service is stopped. Start is, and starts the service first.");

    /// <summary>Whether a VM verb is one the services-down path takes: with vmms down any VM may be
    /// started, since its state cannot be read; with only the Host Compute Service down, one that is off,
    /// saved or paused.</summary>
    private static bool StartableWhileServicesDown(string? state, bool vmmsDown, VmOpKind kind) =>
        (kind is VmOpKind.Start or VmOpKind.Resume)
        && (vmmsDown
            || VmStateUi.ClassifyShape(state) is VmStateUi.Shape.Off or VmStateUi.Shape.Saved or VmStateUi.Shape.Paused);

    /// <summary>Whether a service verb is offered right now — what a service button's availability reads.
    /// The same condition <see cref="Service"/> accepts on, so the button is greyed out exactly when
    /// pressing it would be refused.</summary>
    public static bool ServiceVerbAvailable(HyperVServiceKind kind, HyperVServiceState state, bool start) =>
        (start || ServiceStopGuard.MayStopUnattended(kind))
        && state == (start ? HyperVServiceState.Stopped : HyperVServiceState.Running);

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

        // Through the predicate, so the button's availability and this refusal cannot disagree.
        return ServiceVerbAvailable(kind, state, start)
            ? MqttCommandVerdict.Accept(run)
            : MqttCommandVerdict.Refuse(
                $"'{(start ? "Start" : "Stop")}' is not available while the service is "
                + $"{HyperVServiceNames.StateText(state).ToLowerInvariant()}.");
    }

    /// <summary>The VM's state as a refusal names it.</summary>
    private static string Describe(string? state) =>
        string.IsNullOrWhiteSpace(state) ? "in an unknown state" : state;
}
