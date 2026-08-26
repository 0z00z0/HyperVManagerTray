using HyperVManagerTray.Models;
using ZeroZero.Mqtt;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// What an inbound MQTT command is allowed to do (issue #75). Every VM verb passes through
/// <see cref="VmStateUi.AllowedVerbs"/> — the same gate the dashboard's buttons use — so a remote write
/// can reach nothing the dashboard cannot, and a disallowed verb is refused rather than attempted.
///
/// <para>The refusal sentences are the application's own and are carried verbatim to
/// <see cref="MqttConnectionSetup.CommandRefused"/>. The module composes none: only this app knows why a
/// value it understands is one it will not act on.</para>
/// </summary>
public static class MqttCommandGate
{
    /// <summary>The power verbs announced as select options, in the order the receiver shows them.</summary>
    public static readonly IReadOnlyList<VmOpKind> PowerVerbs =
        [VmOpKind.Start, VmOpKind.Shutdown, VmOpKind.Pause, VmOpKind.Save, VmOpKind.Resume];

    /// <summary>The option strings for the per-VM power select.</summary>
    public static IReadOnlyList<string> PowerOptions => [.. PowerVerbs.Select(v => v.ToString())];

    /// <summary>The announced option back to a verb, or null when the payload names none.</summary>
    public static VmOpKind? ParseVerb(string? payload)
    {
        string trimmed = (payload ?? string.Empty).Trim();
        foreach (var verb in PowerVerbs)
            if (trimmed.Equals(verb.ToString(), StringComparison.OrdinalIgnoreCase)) return verb;
        return null;
    }

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

    /// <summary>Whether a switch-override may run: the name has to be one this host announced, so a
    /// stale option at the receiver cannot bind a switch no rule names.</summary>
    public static MqttCommandVerdict Override(
        IReadOnlyList<string> ruleSwitches, string? switchName, Func<string, CancellationToken, Task> run)
    {
        string trimmed = (switchName ?? string.Empty).Trim();
        return ruleSwitches.Contains(trimmed, StringComparer.OrdinalIgnoreCase)
            ? MqttCommandVerdict.Accept(ct => run(trimmed, ct))
            : MqttCommandVerdict.NotAnOption($"'{trimmed}' is not one of the configured rule switches.");
    }

    /// <summary>The VM's state as a refusal names it.</summary>
    private static string Describe(string? state) =>
        string.IsNullOrWhiteSpace(state) ? "in an unknown state" : state;
}
