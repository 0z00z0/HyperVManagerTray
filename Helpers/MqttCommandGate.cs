using HyperVManagerTray.Models;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// What an inbound Home Assistant command is allowed to do (issue #75). Pure, so the rule a remote
/// write is held to is testable without a broker or a live host.
///
/// <para>Every VM verb passes through <see cref="Power"/>, which asks
/// <see cref="VmStateUi.AllowedVerbs"/> — the same gate the dashboard's buttons use. A remote write
/// therefore reaches nothing the dashboard cannot, and a verb the current state does not allow is
/// refused outright rather than queued or attempted: Hyper-V would answer it with 0x8007 anyway, and
/// an attempt would leave a failure in the log that looks like a fault rather than a refusal.</para>
/// </summary>
public static class MqttCommandGate
{
    /// <summary>The power verbs announced as select options, in the order Home Assistant shows them.</summary>
    public static readonly IReadOnlyList<VmOpKind> PowerVerbs =
        [VmOpKind.Start, VmOpKind.Shutdown, VmOpKind.Pause, VmOpKind.Save, VmOpKind.Resume];

    /// <summary>The option strings for the per-VM power select.</summary>
    public static IReadOnlyList<string> PowerOptions => [.. PowerVerbs.Select(v => v.ToString())];

    /// <summary>Whether a verb may run, and — when it may not — why not.</summary>
    public readonly record struct Verdict(bool Allowed, string Reason)
    {
        public static Verdict Allow() => new(true, string.Empty);
        public static Verdict Refuse(string reason) => new(false, reason);
    }

    /// <summary>The announced option back to a verb, or null when the payload names none.</summary>
    public static VmOpKind? ParseVerb(string? payload)
    {
        string trimmed = (payload ?? string.Empty).Trim();
        foreach (var verb in PowerVerbs)
            if (trimmed.Equals(verb.ToString(), StringComparison.OrdinalIgnoreCase)) return verb;
        return null;
    }

    /// <summary>Whether <paramref name="kind"/> may be requested for a VM currently in
    /// <paramref name="state"/>.</summary>
    public static Verdict Power(string? state, VmOpKind kind) =>
        VmStateUi.AllowedVerbs(state).Contains(kind)
            ? Verdict.Allow()
            : Verdict.Refuse($"'{kind}' is not available while the VM is {Describe(state)}.");

    /// <summary>The verb an on/off switch means, given the VM's current state: on starts a stopped or
    /// saved VM, off shuts a running one down. A state that allows neither refuses.</summary>
    public static Verdict Running(string? state, bool on, out VmOpKind kind)
    {
        kind = on ? VmOpKind.Start : VmOpKind.Shutdown;
        // A paused VM turned on resumes rather than starts — Start is not among its allowed verbs.
        if (on && VmStateUi.ClassifyShape(state) == VmStateUi.Shape.Paused) kind = VmOpKind.Resume;
        return Power(state, kind);
    }

    /// <summary>Whether a switch-override may run: the name has to be one this host actually
    /// announced, so a stale option in Home Assistant cannot bind a switch that no rule names.</summary>
    public static Verdict Override(IReadOnlyList<string> ruleSwitches, string? switchName)
    {
        string trimmed = (switchName ?? string.Empty).Trim();
        return ruleSwitches.Contains(trimmed, StringComparer.OrdinalIgnoreCase)
            ? Verdict.Allow()
            : Verdict.Refuse($"'{trimmed}' is not one of the configured rule switches.");
    }

    /// <summary>The VM's state as a refusal names it.</summary>
    private static string Describe(string? state) =>
        string.IsNullOrWhiteSpace(state) ? "in an unknown state" : state;
}
